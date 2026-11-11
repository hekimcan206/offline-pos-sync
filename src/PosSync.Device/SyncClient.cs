using PosSync.Core.Model;
using PosSync.Core.Protocol;

namespace PosSync.Device;

public sealed record SyncClientOptions
{
    public int PushBatchSize { get; init; } = 100;
    public int PullBatchSize { get; init; } = 500;

    /// <summary>Partial sync: entity types to pull. Null pulls everything in the store.</summary>
    public IReadOnlyList<string>? Entities { get; init; }
}

public sealed record SyncReport(int Pushed, int Duplicates, int Rejected, int Pulled);

/// <summary>
/// One sync round: drain the outbox (oldest first, in batches), then pull until caught up. Any failure leaves every
/// unacknowledged change in the outbox and the pull cursor where it was, so the next round simply repeats the work;
/// the server and the device both discard changes they already have, which makes repetition harmless.
/// </summary>
public sealed class SyncClient(DeviceStore store, ISyncTransport transport, SyncClientOptions? options = null)
{
    private readonly SyncClientOptions _options = options ?? new SyncClientOptions();

    public DeviceStore Store { get; } = store;

    public async Task<SyncReport> SyncOnceAsync(CancellationToken ct = default)
    {
        int pushed = 0, duplicates = 0, rejected = 0, pulled = 0;

        while (true)
        {
            var batch = await Store.PeekOutboxAsync(_options.PushBatchSize, ct);
            if (batch.Count == 0)
            {
                break;
            }

            PushResponse response;
            try
            {
                response = await transport.PushAsync(
                    new PushRequest(Store.Identity.NodeId, Store.Schema.Version, [.. batch.Select(i => i.Change)]), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await Store.MarkAttemptAsync(batch, ex.GetType().Name, CancellationToken.None);
                throw;
            }

            await Store.AcknowledgeAsync(response, ct);
            pushed += response.Accepted.Count;
            duplicates += response.Duplicates.Count;
            rejected += response.Rejected.Count;
        }

        while (true)
        {
            var since = await Store.GetCursorAsync(ct);
            var response = await transport.PullAsync(since, _options.PullBatchSize, _options.Entities, ct);
            pulled += await Store.ApplyPulledAsync(response, ct);
            if (!response.HasMore)
            {
                break;
            }
        }

        return new SyncReport(pushed, duplicates, rejected, pulled);
    }
}

public enum LinkState
{
    Unknown,
    Online,
    Offline,
}

/// <summary>
/// Runs <see cref="SyncClient.SyncOnceAsync"/> forever: every <c>interval</c> while online, with capped exponential
/// backoff and full jitter while offline, so fifty terminals that lose the same uplink do not reconnect in lockstep.
/// </summary>
public sealed class SyncLoop(SyncClient client, TimeSpan interval, TimeSpan? maxBackoff = null, TimeProvider? time = null) : IDisposable
{
    private readonly TimeSpan _maxBackoff = maxBackoff ?? TimeSpan.FromSeconds(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _kick = new(0, 1);

    public LinkState State { get; private set; } = LinkState.Unknown;
    public int ConsecutiveFailures { get; private set; }
    public string? LastError { get; private set; }
    public long Rounds { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }

    public void Dispose() => _kick.Dispose();

    /// <summary>Ask for a sync now (after a local write) instead of waiting for the interval.</summary>
    public void Kick()
    {
        if (_kick.CurrentCount == 0)
        {
            try
            {
                _kick.Release();
            }
            catch (SemaphoreFullException)
            {
                // Another caller kicked first; one pending kick is enough.
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await client.SyncOnceAsync(ct);
                State = LinkState.Online;
                ConsecutiveFailures = 0;
                LastError = null;
                LastSuccess = _time.GetUtcNow();
                delay = interval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SyncRejectedException)
            {
                State = LinkState.Offline;
                ConsecutiveFailures++;
                LastError = ex.GetType().Name;
                delay = Backoff(ConsecutiveFailures);
            }

            Rounds++;
            try
            {
                await _kick.WaitAsync(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan Backoff(int failures)
    {
        // Full jitter (uniform in [0, min(cap, base * 2^n)]): spreads reconnects better than equal or no jitter.
        var ceiling = Math.Min(_maxBackoff.TotalMilliseconds, 100 * Math.Pow(2, Math.Min(failures, 10)));
#pragma warning disable CA5394 // Jitter is not security-sensitive.
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling);
#pragma warning restore CA5394
    }
}

/// <summary>Entity names a terminal subscribes to: tickets, lines and the menu of its own store.</summary>
public static class Subscriptions
{
    public static readonly IReadOnlyList<string> FrontOfHouse =
        [SyncSchema.Entities.Ticket, SyncSchema.Entities.TicketLine, SyncSchema.Entities.MenuItem];

    /// <summary>A kitchen display needs tickets and lines, not the menu.</summary>
    public static readonly IReadOnlyList<string> Kitchen =
        [SyncSchema.Entities.Ticket, SyncSchema.Entities.TicketLine];
}
