using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PosSync.Core.Clock;
using PosSync.Core.Merge;
using PosSync.Core.Model;
using PosSync.Core.Protocol;
using PosSync.Server.Infrastructure;

namespace PosSync.Server.Sync;

public sealed class SyncOptions
{
    public const string Section = "Sync";

    /// <summary>A change stamped further ahead of server time than this is refused (clock_skew).</summary>
    public TimeSpan MaxClockDrift { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxPushBatch { get; set; } = 500;
    public int MaxPullLimit { get; set; } = 1000;

    /// <summary>Key for the admin endpoints (device registration, menu, read models). Empty disables them.</summary>
    public string AdminKey { get; set; } = string.Empty;
}

/// <summary>The device a request was authenticated as.</summary>
public sealed record AuthenticatedDevice(string NodeId, string StoreId);

/// <summary>
/// PostgreSQL side of the protocol. One push is one transaction that (1) locks the store row, which serialises pushes
/// per store and makes sequence order equal commit order, (2) appends the new changes with
/// <c>INSERT ... ON CONFLICT (change_id) DO NOTHING</c>, so the unique index decides what is a replay, and (3) merges
/// only the newly inserted changes into entity state with the same <see cref="MergeEngine"/> the devices run.
/// Every statement is set-based (arrays + unnest): a 500-change push costs five round trips, not 1 500.
/// </summary>
internal sealed class SyncStore(NpgsqlDataSource db, SyncMetrics metrics, IOptions<SyncOptions> options, TimeProvider time)
{
    private readonly SyncSchema _schema = SyncSchema.Default;
    private readonly SyncOptions _options = options.Value;
    private readonly HybridLogicalClock _clock = new(time, maxForwardDrift: options.Value.MaxClockDrift);

    public HybridLogicalClock Clock => _clock;

    public async Task<PushResponse> PushAsync(AuthenticatedDevice device, PushRequest request, CancellationToken ct)
    {
        var rejected = new List<Rejection>();
        var candidates = new List<(Change Change, byte[] Hash)>();
        var seen = new HashSet<Guid>();
        var duplicates = new List<Guid>();

        foreach (var change in request.Changes)
        {
            var code = Check(device, change);
            if (code is not null)
            {
                rejected.Add(new Rejection(change.ChangeId, code.Code, code.Detail));
                metrics.Rejected(code.Code);
                continue;
            }

            if (!seen.Add(change.ChangeId))
            {
                duplicates.Add(change.ChangeId); // the same id twice in one request
                continue;
            }

            candidates.Add((change, BodyHash(change)));
        }

        var accepted = new List<Guid>();
        if (candidates.Count > 0)
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            var head = await LockStoreAsync(conn, tx, device.StoreId, ct);
            var inserted = await InsertChangesAsync(conn, tx, device.StoreId, head, candidates, ct);

            var replays = candidates.Where(c => !inserted.ContainsKey(c.Change.ChangeId)).ToList();
            if (replays.Count > 0)
            {
                var stored = await StoredHashesAsync(conn, tx, [.. replays.Select(r => r.Change.ChangeId)], ct);
                foreach (var (change, hash) in replays)
                {
                    if (stored.TryGetValue(change.ChangeId, out var existing) && existing.AsSpan().SequenceEqual(hash))
                    {
                        duplicates.Add(change.ChangeId);
                    }
                    else
                    {
                        rejected.Add(new Rejection(change.ChangeId, RejectionCodes.ConflictingDuplicate,
                            "A different change with this id is already stored."));
                        metrics.Rejected(RejectionCodes.ConflictingDuplicate);
                    }
                }
            }

            var fresh = candidates.Where(c => inserted.ContainsKey(c.Change.ChangeId))
                .Select(c => (c.Change, Seq: inserted[c.Change.ChangeId]))
                .OrderBy(c => c.Seq)
                .ToList();
            if (fresh.Count > 0)
            {
                await MergeAsync(conn, tx, device.StoreId, fresh, ct);
                await using var bump = new NpgsqlCommand("UPDATE sync.stores SET head = $2 WHERE store_id = $1", conn, tx);
                bump.Parameters.Add(new() { Value = device.StoreId });
                bump.Parameters.Add(new() { Value = fresh[^1].Seq });
                await bump.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            accepted.AddRange(fresh.Select(f => f.Change.ChangeId));
            foreach (var (change, _) in fresh)
            {
                _clock.Observe(change.Hlc);
            }
        }

        metrics.Push(request.Changes.Count, accepted.Count, duplicates.Count);
        return new PushResponse(accepted, duplicates, rejected, _clock.Now().Pack());
    }

    public async Task<PullResponse> PullAsync(AuthenticatedDevice device, long since, int limit, IReadOnlyList<string>? entities, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, _options.MaxPullLimit);
        // One statement, one snapshot: the head and the page are read together, so "no more rows" and "cursor = head"
        // can never disagree. Because pushes commit in sequence order per store, nothing can appear below the cursor later.
        await using var cmd = db.CreateCommand("""
            SELECT s.head, c.seq, c.change_id, c.entity, c.entity_id, c.hlc, c.node_id, c.ops::text
            FROM sync.stores s
            LEFT JOIN LATERAL (
                SELECT * FROM sync.changes c
                WHERE c.store_id = s.store_id AND c.seq > $2 AND ($4::text[] IS NULL OR c.entity = ANY($4))
                ORDER BY c.seq
                LIMIT $3) c ON true
            WHERE s.store_id = $1
            """);
        cmd.Parameters.Add(new() { Value = device.StoreId });
        cmd.Parameters.Add(new() { Value = since });
        cmd.Parameters.Add(new() { Value = limit + 1 });
        cmd.Parameters.Add(new() { Value = entities is { Count: > 0 } ? entities.ToArray() : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });

        long head = 0;
        var page = new List<PulledChange>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                head = reader.GetInt64(0);
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var ops = JsonSerializer.Deserialize<List<FieldOp>>(reader.GetString(7), SyncJson.Options)!;
                page.Add(new PulledChange(reader.GetInt64(1), new Change(
                    reader.GetGuid(2), device.StoreId, reader.GetString(3), reader.GetGuid(4),
                    HlcTimestamp.Unpack(reader.GetInt64(5)), reader.GetString(6), ops)));
            }
        }

        var hasMore = page.Count > limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        metrics.Pulled(page.Count);
        var cursor = hasMore ? page[^1].Seq : Math.Max(since, head);
        return new PullResponse(page, cursor, hasMore, _clock.Now().Pack());
    }

    /// <summary>A change authored by the server itself (menu maintenance from head office). Same log, same merge.</summary>
    public async Task<Change> AuthorAsync(string storeId, string entity, Guid entityId, IReadOnlyList<FieldOp> ops, CancellationToken ct)
    {
        var change = new Change(Guid.NewGuid(), storeId, entity, entityId, _clock.Now(), "server", ops);
        if (ChangeValidator.Validate(_schema, change, fromDevice: false) is { } error)
        {
            throw new ArgumentException($"{error.Code}: {error.Detail}", nameof(ops));
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await EnsureStoreAsync(conn, tx, storeId, ct);
        var head = await LockStoreAsync(conn, tx, storeId, ct);
        var inserted = await InsertChangesAsync(conn, tx, storeId, head, [(change, BodyHash(change))], ct);
        await MergeAsync(conn, tx, storeId, [(change, inserted[change.ChangeId])], ct);
        await using (var bump = new NpgsqlCommand("UPDATE sync.stores SET head = $2 WHERE store_id = $1", conn, tx))
        {
            bump.Parameters.Add(new() { Value = storeId });
            bump.Parameters.Add(new() { Value = inserted[change.ChangeId] });
            await bump.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return change;
    }

    public async Task<IReadOnlyList<EntityState>> StatesAsync(string storeId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT state::text FROM sync.entities WHERE store_id = $1");
        cmd.Parameters.Add(new() { Value = storeId });
        var states = new List<EntityState>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            states.Add(EntityStateJson.Deserialize(reader.GetString(0)));
        }

        return states;
    }

    public async Task<long> HeadAsync(string storeId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT head FROM sync.stores WHERE store_id = $1");
        cmd.Parameters.Add(new() { Value = storeId });
        return await cmd.ExecuteScalarAsync(ct) is long head ? head : 0;
    }

    internal static async Task EnsureStoreAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string storeId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("INSERT INTO sync.stores (store_id) VALUES ($1) ON CONFLICT DO NOTHING", conn, tx);
        cmd.Parameters.Add(new() { Value = storeId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private ValidationError? Check(AuthenticatedDevice device, Change change)
    {
        if (ChangeValidator.Validate(_schema, change, fromDevice: true) is { } error)
        {
            return error;
        }

        // The node id is the tie-break in every LWW comparison and the audit trail of who wrote what: a device may only
        // write as itself, into its own store.
        if (!string.Equals(change.NodeId, device.NodeId, StringComparison.Ordinal) ||
            !string.Equals(change.StoreId, device.StoreId, StringComparison.Ordinal))
        {
            return new(RejectionCodes.Forbidden, "Devices may only write as themselves, into their own store.");
        }

        return _clock.IsWithinDrift(change.Hlc)
            ? null
            : new(RejectionCodes.ClockSkew,
                $"Stamped {change.Hlc.WallMs - time.GetUtcNow().ToUnixTimeMilliseconds()} ms ahead of server time; fix the terminal clock.");
    }

    private static async Task<long> LockStoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string storeId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT head FROM sync.stores WHERE store_id = $1 FOR UPDATE", conn, tx);
        cmd.Parameters.Add(new() { Value = storeId });
        return await cmd.ExecuteScalarAsync(ct) as long?
            ?? throw new InvalidOperationException($"Store '{storeId}' does not exist.");
    }

    private static async Task<Dictionary<Guid, long>> InsertChangesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string storeId, long head, IReadOnlyList<(Change Change, byte[] Hash)> changes, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO sync.changes (store_id, seq, change_id, entity, entity_id, hlc, node_id, ops, body_sha256)
            SELECT $1, $2 + t.ord, t.change_id, t.entity, t.entity_id, t.hlc, t.node_id, t.ops::jsonb, t.body_sha256
            FROM unnest($3::uuid[], $4::text[], $5::uuid[], $6::bigint[], $7::text[], $8::text[], $9::bytea[])
                 WITH ORDINALITY AS t(change_id, entity, entity_id, hlc, node_id, ops, body_sha256, ord)
            ON CONFLICT (change_id) DO NOTHING
            RETURNING change_id, seq
            """, conn, tx);
        cmd.Parameters.Add(new() { Value = storeId });
        cmd.Parameters.Add(new() { Value = head });
        cmd.Parameters.Add(new() { Value = changes.Select(c => c.Change.ChangeId).ToArray() });
        cmd.Parameters.Add(new() { Value = changes.Select(c => c.Change.Entity).ToArray() });
        cmd.Parameters.Add(new() { Value = changes.Select(c => c.Change.EntityId).ToArray() });
        cmd.Parameters.Add(new() { Value = changes.Select(c => c.Change.Hlc.Pack()).ToArray() });
        cmd.Parameters.Add(new() { Value = changes.Select(c => c.Change.NodeId).ToArray() });
        cmd.Parameters.Add(new() { Value = changes.Select(c => JsonSerializer.Serialize(c.Change.Ops, SyncJson.Options)).ToArray() });
        cmd.Parameters.Add(new() { Value = changes.Select(c => c.Hash).ToArray() });

        var inserted = new Dictionary<Guid, long>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            inserted[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        return inserted;
    }

    private static async Task<Dictionary<Guid, byte[]>> StoredHashesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid[] ids, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT change_id, body_sha256 FROM sync.changes WHERE change_id = ANY($1)", conn, tx);
        cmd.Parameters.Add(new() { Value = ids });
        var result = new Dictionary<Guid, byte[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetGuid(0)] = (byte[])reader.GetValue(1);
        }

        return result;
    }

    private async Task MergeAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string storeId, IReadOnlyList<(Change Change, long Seq)> fresh, CancellationToken ct)
    {
        var keys = fresh.Select(f => (f.Change.Entity, f.Change.EntityId)).Distinct().ToList();
        var states = new Dictionary<(string, Guid), (EntityState State, long Seq)>();

        await using (var load = new NpgsqlCommand("""
            SELECT e.entity, e.entity_id, e.state::text
            FROM sync.entities e
            JOIN unnest($2::text[], $3::uuid[]) AS k(entity, entity_id) USING (entity, entity_id)
            WHERE e.store_id = $1
            """, conn, tx))
        {
            load.Parameters.Add(new() { Value = storeId });
            load.Parameters.Add(new() { Value = keys.Select(k => k.Entity).ToArray() });
            load.Parameters.Add(new() { Value = keys.Select(k => k.EntityId).ToArray() });
            await using var reader = await load.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                states[(reader.GetString(0), reader.GetGuid(1))] = (EntityStateJson.Deserialize(reader.GetString(2)), 0);
            }
        }

        foreach (var (change, seq) in fresh)
        {
            var key = (change.Entity, change.EntityId);
            var state = states.TryGetValue(key, out var s) ? s.State : new EntityState(storeId, change.Entity, change.EntityId);
            MergeEngine.Apply(_schema.Get(change.Entity), state, change);
            states[key] = (state, seq);
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO sync.entities (store_id, entity, entity_id, state, updated_seq)
            SELECT $1, t.entity, t.entity_id, t.state::jsonb, t.seq
            FROM unnest($2::text[], $3::uuid[], $4::text[], $5::bigint[]) AS t(entity, entity_id, state, seq)
            ON CONFLICT (store_id, entity, entity_id)
            DO UPDATE SET state = excluded.state, updated_seq = excluded.updated_seq
            """, conn, tx);
        var rows = states.ToList();
        upsert.Parameters.Add(new() { Value = storeId });
        upsert.Parameters.Add(new() { Value = rows.Select(r => r.Key.Item1).ToArray() });
        upsert.Parameters.Add(new() { Value = rows.Select(r => r.Key.Item2).ToArray() });
        upsert.Parameters.Add(new() { Value = rows.Select(r => EntityStateJson.Serialize(r.Value.State)).ToArray() });
        upsert.Parameters.Add(new() { Value = rows.Select(r => r.Value.Seq).ToArray() });
        await upsert.ExecuteNonQueryAsync(ct);
    }

    private static byte[] BodyHash(Change change) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(change, SyncJson.Options));
}
