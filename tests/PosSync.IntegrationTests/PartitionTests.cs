using System.Diagnostics;
using System.Net.Http.Json;
using PosSync.Chaos;
using PosSync.Core.Model;
using PosSync.Core.Protocol;
using PosSync.Core.Tickets;
using PosSync.Device;
using PosSync.IntegrationTests.Infrastructure;

namespace PosSync.IntegrationTests;

/// <summary>
/// Network faults injected with Toxiproxy between real terminals and the real server. The promise under test:
/// whatever the network does, every ticket a cashier rang up reaches the server exactly once, and every terminal ends
/// with the same state as the server.
/// </summary>
public sealed class PartitionTests(SyncEnvironment env) : IAsyncLifetime
{
    private const string Link = SyncEnvironment.Link;

    public async ValueTask InitializeAsync() => await env.Toxiproxy.ResetAsync();

    public async ValueTask DisposeAsync() => await env.Toxiproxy.ResetAsync();

    [Fact]
    public async Task Partition_terminals_keep_selling_and_nothing_is_lost_after_the_heal()
    {
        var store = Ids.Store();
        var terminals = await TerminalsAsync(store, 3);
        await env.Toxiproxy.SetEnabledAsync(Link, false);

        var opened = new List<Guid>();
        foreach (var t in terminals)
        {
            for (var i = 0; i < 10; i++)
            {
                var ticket = await t.Pos.OpenTicketAsync($"{i + 1}", 2);
                await t.Pos.AddLineAsync(ticket, Menu.Soup);
                await t.Pos.AddLineAsync(ticket, Menu.Tea, 2);
                opened.Add(ticket);
            }

            var offline = async () => await t.Sync.SyncOnceAsync();
            await offline.Should().ThrowAsync<HttpRequestException>("the uplink is partitioned");
            (await t.Store.PendingCountAsync()).Should().Be(30);
        }

        await env.Toxiproxy.SetEnabledAsync(Link, true);
        await HealAsync(terminals);

        var tickets = await TicketsAsync(store);
        tickets.Select(t => t.Id).Should().BeEquivalentTo(opened, "every ticket rung up during the partition reached the server");
        tickets.Should().OnlyContain(t => t.TotalMinor == 12_000);
        (await CountChangesAsync(store)).Should().Be(90);
        await AssertConvergedAsync(store, terminals);
        await DisposeAllAsync(terminals);
    }

    [Fact]
    public async Task Blackholed_link_times_out_and_the_outbox_is_retained()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1", env.LinkUri, timeout: TimeSpan.FromSeconds(1));
        // timeout=0: bytes are swallowed and the connection hangs (a dead Wi-Fi bridge), unlike a refused connection.
        await env.Toxiproxy.AddToxicAsync(Link, Toxic.Timeout("blackhole", 0));
        var ticket = await t1.Pos.OpenTicketAsync("4", 2);
        await t1.Pos.AddLineAsync(ticket, Menu.Kebab);

        var hung = async () => await t1.Sync.SyncOnceAsync();
        await hung.Should().ThrowAsync<TaskCanceledException>();
        (await t1.Store.PendingCountAsync()).Should().Be(2);
        (await t1.Store.PeekOutboxAsync(10)).Should().OnlyContain(i => i.Attempts == 1);

        await env.Toxiproxy.RemoveToxicAsync(Link, "blackhole");
        await t1.SyncUntilSuccessAsync(TimeSpan.FromSeconds(20));

        (await TicketsAsync(store)).Single().Id.Should().Be(ticket);
        (await t1.Store.PendingCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Response_cut_after_the_server_committed_is_replayed_as_duplicates()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1", env.LinkUri, timeout: TimeSpan.FromSeconds(5));
        var ticket = await t1.Pos.OpenTicketAsync("8", 3);
        var line = await t1.Pos.AddLineAsync(ticket, Menu.Soup);
        await t1.Pos.ChangeQtyAsync(line, +1);
        await t1.Pos.ChangeQtyAsync(line, +1);
        await t1.Pos.PayAsync(ticket);

        // The request reaches the server; the response is cut after a few bytes: the classic lost acknowledgement.
        await env.Toxiproxy.AddToxicAsync(Link, Toxic.LimitData("cut-response", 16));
        var lost = async () => await t1.Sync.SyncOnceAsync();
        await lost.Should().ThrowAsync<Exception>();
        (await CountChangesAsync(store)).Should().Be(5, "the server committed the batch");
        (await t1.Store.PendingCountAsync()).Should().Be(5, "the terminal never saw the acknowledgement");

        await env.Toxiproxy.RemoveToxicAsync(Link, "cut-response");
        var report = await t1.SyncUntilSuccessAsync(TimeSpan.FromSeconds(20));

        report.Duplicates.Should().Be(5);
        report.Pushed.Should().Be(0);
        (await CountChangesAsync(store)).Should().Be(5);
        var server = (await TicketsAsync(store)).Single();
        server.Lines.Single().Qty.Should().Be(3, "each +1 counted once despite the replay");
        server.Status.Should().Be(SyncSchema.TicketStatus.Paid);
    }

    [Fact]
    public async Task Latency_beyond_the_client_timeout_causes_retries_that_are_deduplicated()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1", env.LinkUri, timeout: TimeSpan.FromMilliseconds(800));
        for (var i = 0; i < 4; i++)
        {
            await t1.Pos.OpenTicketAsync($"{i}", 2);
        }

        // Responses arrive 2 s late: the request commits, the client gives up at 800 ms.
        await env.Toxiproxy.AddToxicAsync(Link, Toxic.Latency("slow-responses", 2_000));
        var slow = async () => await t1.Sync.SyncOnceAsync();
        await slow.Should().ThrowAsync<TaskCanceledException>();
        await env.Toxiproxy.RemoveToxicAsync(Link, "slow-responses");

        // A merely slow link (300 ± 100 ms each way) is fine once the timeout covers it.
        await env.Toxiproxy.AddToxicAsync(Link, Toxic.Latency("jitter-down", 300, 100));
        await env.Toxiproxy.AddToxicAsync(Link, Toxic.Latency("jitter-up", 300, 100, stream: "upstream"));
        await using var patient = await TestTerminal.CreateAsync(env, store, "T2", env.LinkUri, timeout: TimeSpan.FromSeconds(10));
        await patient.Pos.OpenTicketAsync("40", 2);
        var t1Retry = new SyncClient(t1.Store, t1.TransportVia(env.LinkUri, TimeSpan.FromSeconds(10)));
        var report = await t1Retry.SyncOnceAsync();
        await patient.Sync.SyncOnceAsync();
        await t1Retry.SyncOnceAsync();

        report.Duplicates.Should().Be(4, "the timed-out push had been committed");
        (await CountChangesAsync(store)).Should().Be(5);
        (await t1.Store.HashAsync()).Should().Be(await ServerHashAsync(store));
        (await patient.Store.HashAsync()).Should().Be(await ServerHashAsync(store));
    }

    [Fact]
    public async Task Flapping_link_with_connection_resets_converges_exactly_once()
    {
        var store = Ids.Store();
        var terminals = await TerminalsAsync(store, 3, timeout: TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource();
        var loops = terminals.Select(t => new SyncLoop(t.Sync, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(800))).ToList();
        var loopTasks = loops.Select(l => l.RunAsync(stop.Token)).ToList();
        foreach (var (t, loop) in terminals.Zip(loops))
        {
            t.Pos.Changed += loop.Kick;
        }

        // The link flaps every 100-500 ms for six seconds; some "up" periods reset connections mid-flight.
        using var flapping = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var flapper = Task.Run(async () =>
        {
            var rng = new Random(42);
            var up = true;
            while (!flapping.IsCancellationRequested)
            {
                up = !up;
                await env.Toxiproxy.SetEnabledAsync(Link, up);
                if (up && rng.Next(3) == 0)
                {
                    await env.Toxiproxy.AddToxicAsync(Link, Toxic.ResetPeer("rst", rng.Next(20, 200)));
                }
                else if (!up)
                {
                    await env.Toxiproxy.ResetAsync();
                    await env.Toxiproxy.SetEnabledAsync(Link, false);
                }

                await Task.Delay(rng.Next(100, 500));
            }
        });

        var opened = new List<Guid>();
        var writers = terminals.Select(async (t, n) =>
        {
            var mine = new List<Guid>();
            while (!flapping.IsCancellationRequested)
            {
                var ticket = await t.Pos.OpenTicketAsync($"{n}-{mine.Count}", 2);
                var line = await t.Pos.AddLineAsync(ticket, Menu.All[mine.Count % 3]);
                await t.Pos.ChangeQtyAsync(line, +1);
                mine.Add(ticket);
                await Task.Delay(40);
            }

            return mine;
        }).ToList();
        var flapStart = Stopwatch.StartNew();
        foreach (var w in writers)
        {
            opened.AddRange(await w);
        }

        await flapper;
        TestContext.Current.TestOutputHelper?.WriteLine($"flapping phase {flapStart.ElapsedMilliseconds} ms");
        await env.Toxiproxy.ResetAsync();

        var sw = Stopwatch.StartNew();
        await WaitUntilAsync(async () =>
        {
            var serverHash = await ServerHashAsync(store);
            foreach (var t in terminals)
            {
                if (await t.Store.PendingCountAsync() > 0 || await t.Store.HashAsync() != serverHash)
                {
                    return false;
                }
            }

            return true;
        }, TimeSpan.FromSeconds(45));
        sw.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"converged {sw.ElapsedMilliseconds} ms after the link stabilised; {opened.Count} tickets");
        await stop.CancelAsync();
        await Task.WhenAll(loopTasks);

        opened.Count.Should().BeGreaterThan(30, "the writers kept selling while the link flapped");
        var tickets = await TicketsAsync(store);
        tickets.Select(t => t.Id).Should().BeEquivalentTo(opened);
        tickets.Should().OnlyContain(t => t.Lines.Single().Qty == 2, "each +1 applied exactly once");
        (await CountChangesAsync(store)).Should().Be(opened.Count * 3);
        loops.Should().OnlyContain(l => l.Rounds > 0);
        foreach (var loop in loops)
        {
            loop.Dispose();
        }

        await DisposeAllAsync(terminals);
    }

    [Fact]
    public async Task Requests_reordered_in_flight_still_merge_in_HLC_order()
    {
        var store = Ids.Store();
        await using var t1 = await TestTerminal.CreateAsync(env, store, "T1", env.LinkUri);
        await using var t2 = await TestTerminal.CreateAsync(env, store, "T2", env.LinkUri);
        var ticket = await t1.Pos.OpenTicketAsync("11", 2);
        await t1.Sync.SyncOnceAsync();

        var first = await t1.Store.WriteAsync(SyncSchema.Entities.Ticket, ticket, _ => [FieldOp.Set("note", "first")]);
        var second = await t1.Store.WriteAsync(SyncSchema.Entities.Ticket, ticket, _ => [FieldOp.Set("note", "second")]);
        second.Hlc.Should().BeGreaterThan(first.Hlc);

        // TCP keeps bytes in order inside one connection, so reordering is produced the way it happens in practice:
        // the older request travels a slow path (a retry on a degraded connection) and the newer one a fast path.
        await env.Toxiproxy.AddToxicAsync(SyncEnvironment.SlowLink, Toxic.Latency("slow-path", 1_500, stream: "upstream"));
        var slow = t1.TransportVia(env.SlowLinkUri, TimeSpan.FromSeconds(10));
        var olderPush = slow.PushAsync(new PushRequest(t1.Store.Identity.NodeId, 1, [first]), default);
        await Task.Delay(200);
        var newerPush = t1.Transport.PushAsync(new PushRequest(t1.Store.Identity.NodeId, 1, [second]), default);
        await Task.WhenAll(olderPush, newerPush);

        var seqs = await SeqsAsync(store, first.ChangeId, second.ChangeId);
        seqs[second.ChangeId].Should().BeLessThan(seqs[first.ChangeId], "the newer change really arrived first");
        await t1.Sync.SyncOnceAsync();
        await t2.Sync.SyncOnceAsync();
        (await TicketsAsync(store)).Single().Note.Should().Be("second", "merge order is HLC order, not arrival order");
        (await t2.Pos.TicketsAsync()).Single().Note.Should().Be("second");
    }

    private async Task<List<TestTerminal>> TerminalsAsync(string store, int count, TimeSpan? timeout = null)
    {
        var list = new List<TestTerminal>();
        for (var i = 1; i <= count; i++)
        {
            list.Add(await TestTerminal.CreateAsync(env, store, $"T{i}", env.LinkUri, timeout ?? TimeSpan.FromSeconds(3)));
        }

        return list;
    }

    private static async Task HealAsync(IReadOnlyList<TestTerminal> terminals)
    {
        // Two rounds: the first pushes everyone's backlog, the second pulls what the others pushed after us.
        for (var round = 0; round < 2; round++)
        {
            foreach (var t in terminals)
            {
                await t.SyncUntilSuccessAsync(TimeSpan.FromSeconds(20));
            }
        }
    }

    private async Task AssertConvergedAsync(string store, IReadOnlyList<TestTerminal> terminals)
    {
        var serverHash = await ServerHashAsync(store);
        foreach (var t in terminals)
        {
            (await t.Store.HashAsync()).Should().Be(serverHash, $"{t.Store.Identity.NodeId} converged with the server");
            (await t.Store.PendingCountAsync()).Should().Be(0);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(until, "the system must converge after the link stabilises");
            await Task.Delay(100);
        }
    }

    private static async Task DisposeAllAsync(IEnumerable<TestTerminal> terminals)
    {
        foreach (var t in terminals)
        {
            await t.DisposeAsync();
        }
    }

    private async Task<string> ServerHashAsync(string store)
    {
        using var admin = env.Admin();
        return (await admin.GetFromJsonAsync<StoreState>($"api/v1/admin/stores/{store}/state"))!.Hash;
    }

    private async Task<IReadOnlyList<TicketView>> TicketsAsync(string store)
    {
        using var admin = env.Admin();
        return (await admin.GetFromJsonAsync<List<TicketView>>($"api/v1/admin/stores/{store}/tickets"))!;
    }

    private async Task<long> CountChangesAsync(string store)
    {
        await using var ds = env.DataSource(env.SuperuserConnectionString);
        await using var cmd = ds.CreateCommand("SELECT count(*) FROM sync.changes WHERE store_id = $1");
        cmd.Parameters.Add(new() { Value = store });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Dictionary<Guid, long>> SeqsAsync(string store, params Guid[] ids)
    {
        await using var ds = env.DataSource(env.SuperuserConnectionString);
        await using var cmd = ds.CreateCommand("SELECT change_id, seq FROM sync.changes WHERE store_id = $1 AND change_id = ANY($2)");
        cmd.Parameters.Add(new() { Value = store });
        cmd.Parameters.Add(new() { Value = ids });
        var result = new Dictionary<Guid, long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return result;
    }

    private sealed record StoreState(string StoreId, long Head, int Entities, string Hash);
}
