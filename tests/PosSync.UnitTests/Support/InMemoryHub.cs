using System.Text.Json;
using PosSync.Core.Clock;
using PosSync.Core.Merge;
using PosSync.Core.Model;
using PosSync.Core.Protocol;
using PosSync.Device;

namespace PosSync.UnitTests.Support;

/// <summary>
/// The server's sync semantics without PostgreSQL: a per-store append-only log with a dense sequence, change-id
/// deduplication, scope and drift checks, and a merged replica per store. The PostgreSQL server is tested against
/// the same expectations in the integration suite; this one lets property tests run thousands of schedules.
/// </summary>
internal sealed class InMemoryHub(SyncSchema schema, TimeProvider time)
{
    private readonly Dictionary<string, List<PulledChange>> _logs = [];
    private readonly Dictionary<Guid, string> _bodies = [];
    private readonly Dictionary<string, InMemoryReplica> _replicas = [];
    private readonly Lock _gate = new();

    public HybridLogicalClock Clock { get; } = new(time, maxForwardDrift: TimeSpan.FromMinutes(5));

    public int StoredChanges
    {
        get
        {
            lock (_gate)
            {
                return _bodies.Count;
            }
        }
    }

    public bool ContainsChange(Guid id)
    {
        lock (_gate)
        {
            return _bodies.ContainsKey(id);
        }
    }

    public InMemoryReplica Replica(string store)
    {
        lock (_gate)
        {
            return ReplicaFor(store);
        }
    }

    public PushResponse Push(DeviceIdentity device, PushRequest request)
    {
        lock (_gate)
        {
            List<Guid> accepted = [], duplicates = [];
            List<Rejection> rejected = [];
            foreach (var change in request.Changes)
            {
                if (change.StoreId != device.StoreId || change.NodeId != device.NodeId)
                {
                    rejected.Add(new(change.ChangeId, RejectionCodes.Forbidden, "scope"));
                    continue;
                }

                if (ChangeValidator.Validate(schema, change, fromDevice: true) is { } error)
                {
                    rejected.Add(new(change.ChangeId, error.Code, error.Detail));
                    continue;
                }

                var body = JsonSerializer.Serialize(change, SyncJson.Options);
                if (_bodies.TryGetValue(change.ChangeId, out var existing))
                {
                    if (existing == body)
                    {
                        duplicates.Add(change.ChangeId);
                    }
                    else
                    {
                        rejected.Add(new(change.ChangeId, RejectionCodes.ConflictingDuplicate, "body differs"));
                    }

                    continue;
                }

                if (!Clock.IsWithinDrift(change.Hlc))
                {
                    rejected.Add(new(change.ChangeId, RejectionCodes.ClockSkew, "ahead"));
                    continue;
                }

                Clock.Observe(change.Hlc);
                Append(change, body);
                accepted.Add(change.ChangeId);
            }

            return new PushResponse(accepted, duplicates, rejected, Clock.Now().Pack());
        }
    }

    /// <summary>A change authored by the server itself (menu maintenance).</summary>
    public Change Author(string store, string entity, Guid id, IReadOnlyList<FieldOp> ops)
    {
        lock (_gate)
        {
            var change = new Change(Guid.NewGuid(), store, entity, id, Clock.Now(), "server", ops);
            Append(change, JsonSerializer.Serialize(change, SyncJson.Options));
            return change;
        }
    }

    public PullResponse Pull(string store, long since, int limit, IReadOnlyList<string>? entities)
    {
        lock (_gate)
        {
            var log = _logs.GetValueOrDefault(store) ?? [];
            var head = log.Count == 0 ? 0 : log[^1].Seq;
            var page = log.Where(p => p.Seq > since && (entities is null || entities.Contains(p.Change.Entity)))
                .Take(limit + 1).ToList();
            var hasMore = page.Count > limit;
            if (hasMore)
            {
                page.RemoveAt(page.Count - 1);
            }

            var cursor = hasMore ? page[^1].Seq : Math.Max(since, head);
            return new PullResponse(page, cursor, hasMore, Clock.Now().Pack());
        }
    }

    private void Append(Change change, string body)
    {
        var log = _logs.TryGetValue(change.StoreId, out var l) ? l : _logs[change.StoreId] = [];
        log.Add(new PulledChange(log.Count + 1, change));
        _bodies[change.ChangeId] = body;
        ReplicaFor(change.StoreId).Apply(change);
    }

    private InMemoryReplica ReplicaFor(string store) =>
        _replicas.TryGetValue(store, out var r) ? r : _replicas[store] = new InMemoryReplica(schema);
}

/// <summary>A device's link to the hub, with the failure modes the property and fault tests inject.</summary>
internal sealed class HubTransport(InMemoryHub hub, DeviceIdentity device) : ISyncTransport
{
    public bool Online { get; set; } = true;

    /// <summary>The hub commits the next push but the response never arrives (the classic lost ack).</summary>
    public bool DropNextPushResponse { get; set; }

    public PullResponse? LastPull { get; private set; }

    public Task<PushResponse> PushAsync(PushRequest request, CancellationToken ct)
    {
        if (!Online)
        {
            throw new HttpRequestException("link down");
        }

        var response = hub.Push(device, request);
        if (DropNextPushResponse)
        {
            DropNextPushResponse = false;
            throw new HttpRequestException("response lost");
        }

        return Task.FromResult(response);
    }

    public Task<PullResponse> PullAsync(long since, int limit, IReadOnlyList<string>? entities, CancellationToken ct)
    {
        if (!Online)
        {
            throw new HttpRequestException("link down");
        }

        LastPull = hub.Pull(device.StoreId, since, limit, entities);
        return Task.FromResult(LastPull);
    }
}
