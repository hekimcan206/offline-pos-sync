using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PosSync.Core.Clock;
using PosSync.Core.Merge;
using PosSync.Core.Model;
using PosSync.Core.Protocol;

namespace PosSync.Device;

/// <summary>Who this terminal is: its node id (unique per device, used as the HLC tie-break) and its store.</summary>
public sealed record DeviceIdentity(string NodeId, string StoreId);

/// <summary>A change waiting in the outbox.</summary>
public sealed record OutboxItem(long Seq, Change Change, int Attempts);

/// <summary>A change the server refused. It stays on the device: nothing a cashier rang up is ever deleted.</summary>
public sealed record RejectedItem(long Seq, Change Change, string Code, string Detail);

/// <summary>
/// The terminal database (SQLite). One local transaction writes the merged entity state, the applied-change id and
/// the outbox row together, so a ticket is durable the moment the cashier rings it up, whether or not the network is
/// up, and a crash can never leave a state change without the outbox entry that will replicate it.
/// </summary>
/// <remarks>
/// SQLite runs in WAL mode with <c>synchronous=FULL</c>: every commit is fsynced. NORMAL would be faster but can lose
/// the last transactions on power loss, and power loss is a normal event for a terminal under a bar counter.
/// The connection is single and guarded by a semaphore; the UI and the sync loop share it.
/// </remarks>
public sealed class DeviceStore : IAsyncDisposable
{
    private const string Pending = "pending";
    private const string RejectedStatus = "rejected";

    private readonly SqliteConnection _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DeviceStore(SqliteConnection db, DeviceIdentity identity, SyncSchema schema, HybridLogicalClock clock)
    {
        _db = db;
        Identity = identity;
        Schema = schema;
        Clock = clock;
    }

    public DeviceIdentity Identity { get; }
    public SyncSchema Schema { get; }
    public HybridLogicalClock Clock { get; }

    /// <summary>
    /// Opens (or creates) the database at <paramref name="path"/>. The HLC resumes from the last persisted value, so a
    /// wall clock that went backwards while the terminal was off cannot make it reissue old timestamps.
    /// </summary>
    /// <param name="deviceMaxDrift">
    /// How far ahead of local time a pulled timestamp may be. Generous on purpose: the server already enforces the
    /// strict bound, and a terminal whose own clock is slow must still accept (and catch up to) correct timestamps.
    /// </param>
    public static async Task<DeviceStore> OpenAsync(
        string path, DeviceIdentity identity, SyncSchema schema, TimeProvider time,
        TimeSpan? deviceMaxDrift = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        await db.OpenAsync(ct);
        await ExecAsync(db, null, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entities (
                entity    TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                store_id  TEXT NOT NULL,
                state     TEXT NOT NULL,
                PRIMARY KEY (entity, entity_id));
            CREATE TABLE IF NOT EXISTS applied_changes (
                change_id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS outbox (
                seq        INTEGER PRIMARY KEY AUTOINCREMENT,
                change_id  TEXT    NOT NULL UNIQUE,
                body       TEXT    NOT NULL,
                status     TEXT    NOT NULL DEFAULT 'pending',
                attempts   INTEGER NOT NULL DEFAULT 0,
                last_error TEXT);
            CREATE INDEX IF NOT EXISTS ix_outbox_status ON outbox (status, seq);
            """, ct);

        var storedNode = await GetMetaAsync(db, "node_id", ct);
        if (storedNode is null)
        {
            await SetMetaAsync(db, null, "node_id", identity.NodeId, ct);
            await SetMetaAsync(db, null, "store_id", identity.StoreId, ct);
        }
        else if (storedNode != identity.NodeId)
        {
            // Reusing a database under another node id would break the tie-break order and idempotency assumptions.
            throw new InvalidOperationException($"Database belongs to node '{storedNode}', not '{identity.NodeId}'.");
        }

        var hlc = await GetMetaAsync(db, "hlc", ct) is { } packed
            ? HlcTimestamp.Unpack(long.Parse(packed, CultureInfo.InvariantCulture))
            : HlcTimestamp.Zero;
        var clock = new HybridLogicalClock(time, hlc, deviceMaxDrift ?? TimeSpan.FromDays(1));
        return new DeviceStore(db, identity, schema, clock);
    }

    /// <summary>
    /// Records a local edit: stamps it, merges it into local state and queues it for upload, atomically.
    /// <paramref name="edit"/> receives the current local state and returns the intended operations.
    /// </summary>
    public async Task<Change> WriteAsync(
        string entity, Guid entityId, Func<EntityState?, IReadOnlyList<FieldOp>> edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var spec = Schema.Get(entity);
        await _gate.WaitAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct);
            var state = await LoadAsync(tx, entity, entityId, ct);
            var ops = Core.Tickets.TicketOps.ForStrategy(spec, state, edit(state));
            var change = new Change(Guid.NewGuid(), Identity.StoreId, entity, entityId, Clock.Now(), Identity.NodeId, ops);
            if (ChangeValidator.Validate(Schema, change, fromDevice: true) is { } error)
            {
                throw new ArgumentException($"{error.Code}: {error.Detail}", nameof(edit));
            }

            state ??= new EntityState(Identity.StoreId, entity, entityId);
            MergeEngine.Apply(spec, state, change);
            await SaveAsync(tx, state, ct);
            await MarkAppliedAsync(tx, change.ChangeId, ct);
            await using (var cmd = Command(tx, "INSERT INTO outbox (change_id, body) VALUES ($id, $body)"))
            {
                cmd.Parameters.AddWithValue("$id", change.ChangeId.ToString());
                cmd.Parameters.AddWithValue("$body", JsonSerializer.Serialize(change, SyncJson.Options));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await SetMetaAsync(_db, tx, "hlc", change.Hlc.Pack().ToString(CultureInfo.InvariantCulture), ct);
            await tx.CommitAsync(ct);
            return change;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The oldest pending outbox items, in the order they were written.</summary>
    public async Task<IReadOnlyList<OutboxItem>> PeekOutboxAsync(int max, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = Command(null, "SELECT seq, body, attempts FROM outbox WHERE status = 'pending' ORDER BY seq LIMIT $max");
            cmd.Parameters.AddWithValue("$max", max);
            var items = new List<OutboxItem>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new OutboxItem(reader.GetInt64(0), Deserialize(reader.GetString(1)), reader.GetInt32(2)));
            }

            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Counts an attempt for the given items (they stay pending until acknowledged).</summary>
    public async Task MarkAttemptAsync(IReadOnlyList<OutboxItem> items, string? error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        await _gate.WaitAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct);
            foreach (var item in items)
            {
                await using var cmd = Command(tx, "UPDATE outbox SET attempts = attempts + 1, last_error = $err WHERE seq = $seq");
                cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$seq", item.Seq);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies a push response: accepted and duplicate ids leave the outbox (the server holds them durably), rejected
    /// ones are kept with their reason. Also advances the local HLC past the server clock.
    /// </summary>
    public async Task AcknowledgeAsync(PushResponse response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        await _gate.WaitAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct);
            foreach (var id in response.Accepted.Concat(response.Duplicates))
            {
                await using var cmd = Command(tx, "DELETE FROM outbox WHERE change_id = $id");
                cmd.Parameters.AddWithValue("$id", id.ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            foreach (var rejection in response.Rejected)
            {
                await using var cmd = Command(tx, "UPDATE outbox SET status = 'rejected', last_error = $err WHERE change_id = $id");
                cmd.Parameters.AddWithValue("$err", $"{rejection.Code}: {rejection.Detail}");
                cmd.Parameters.AddWithValue("$id", rejection.ChangeId.ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            ObserveServer(response.ServerHlc);
            await SetMetaAsync(_db, tx, "hlc", Clock.Current.Pack().ToString(CultureInfo.InvariantCulture), ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Applies pulled changes and advances the pull cursor in one transaction. Changes this device already has (its own,
    /// or ones pulled before a crash) are skipped by id. Returns how many were new.
    /// </summary>
    public async Task<int> ApplyPulledAsync(PullResponse response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        await _gate.WaitAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _db.BeginTransactionAsync(ct);
            var applied = 0;
            foreach (var pulled in response.Changes)
            {
                var change = pulled.Change;
                Clock.Observe(change.Hlc);
                if (!await MarkAppliedAsync(tx, change.ChangeId, ct))
                {
                    continue;
                }

                var state = await LoadAsync(tx, change.Entity, change.EntityId, ct)
                    ?? new EntityState(change.StoreId, change.Entity, change.EntityId);
                MergeEngine.Apply(Schema.Get(change.Entity), state, change);
                await SaveAsync(tx, state, ct);
                applied++;
            }

            ObserveServer(response.ServerHlc);
            await SetMetaAsync(_db, tx, "cursor", response.Cursor.ToString(CultureInfo.InvariantCulture), ct);
            await SetMetaAsync(_db, tx, "hlc", Clock.Current.Pack().ToString(CultureInfo.InvariantCulture), ct);
            await tx.CommitAsync(ct);
            return applied;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetCursorAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await GetMetaAsync(_db, "cursor", ct) is { } v ? long.Parse(v, CultureInfo.InvariantCulture) : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EntityState?> GetAsync(string entity, Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadAsync(null, entity, id, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<EntityState>> AllStatesAsync(string? entity = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = Command(null, entity is null
                ? "SELECT state FROM entities"
                : "SELECT state FROM entities WHERE entity = $entity");
            if (entity is not null)
            {
                cmd.Parameters.AddWithValue("$entity", entity);
            }

            var states = new List<EntityState>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                states.Add(EntityStateJson.Deserialize(reader.GetString(0)));
            }

            return states;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> PendingCountAsync(CancellationToken ct = default) => await CountAsync(Pending, ct);

    public async Task<IReadOnlyList<RejectedItem>> RejectedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = Command(null, "SELECT seq, body, last_error FROM outbox WHERE status = 'rejected' ORDER BY seq");
            var items = new List<RejectedItem>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var error = reader.IsDBNull(2) ? ": " : reader.GetString(2);
                var split = error.IndexOf(": ", StringComparison.Ordinal);
                items.Add(new RejectedItem(reader.GetInt64(0), Deserialize(reader.GetString(1)),
                    split < 0 ? error : error[..split], split < 0 ? string.Empty : error[(split + 2)..]));
            }

            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Hash of every entity state on this device; equal to the server's hash once converged.</summary>
    public async Task<string> HashAsync(CancellationToken ct = default) =>
        EntityStateJson.Hash(await AllStatesAsync(ct: ct));

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _gate.Dispose();
    }

    private void ObserveServer(long serverHlc)
    {
        if (serverHlc > 0)
        {
            Clock.Observe(HlcTimestamp.Unpack(serverHlc));
        }
    }

    private async Task<int> CountAsync(string status, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var cmd = Command(null, "SELECT COUNT(*) FROM outbox WHERE status = $status");
            cmd.Parameters.AddWithValue("$status", status);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<EntityState?> LoadAsync(SqliteTransaction? tx, string entity, Guid id, CancellationToken ct)
    {
        await using var cmd = Command(tx, "SELECT state FROM entities WHERE entity = $entity AND entity_id = $id");
        cmd.Parameters.AddWithValue("$entity", entity);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return await cmd.ExecuteScalarAsync(ct) is string json ? EntityStateJson.Deserialize(json) : null;
    }

    private async Task SaveAsync(SqliteTransaction tx, EntityState state, CancellationToken ct)
    {
        await using var cmd = Command(tx, """
            INSERT INTO entities (entity, entity_id, store_id, state) VALUES ($entity, $id, $store, $state)
            ON CONFLICT (entity, entity_id) DO UPDATE SET state = excluded.state
            """);
        cmd.Parameters.AddWithValue("$entity", state.Entity);
        cmd.Parameters.AddWithValue("$id", state.EntityId.ToString());
        cmd.Parameters.AddWithValue("$store", state.StoreId);
        cmd.Parameters.AddWithValue("$state", EntityStateJson.Serialize(state));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<bool> MarkAppliedAsync(SqliteTransaction tx, Guid changeId, CancellationToken ct)
    {
        await using var cmd = Command(tx, "INSERT OR IGNORE INTO applied_changes (change_id) VALUES ($id)");
        cmd.Parameters.AddWithValue("$id", changeId.ToString());
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private SqliteCommand Command(SqliteTransaction? tx, string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static Change Deserialize(string body) =>
        JsonSerializer.Deserialize<Change>(body, SyncJson.Options)
        ?? throw new InvalidDataException("Outbox row holds an empty change.");

    private static async Task ExecAsync(SqliteConnection db, SqliteTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> GetMetaAsync(SqliteConnection db, string key, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task SetMetaAsync(SqliteConnection db, SqliteTransaction? tx, string key, string value, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
