using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PosSync.Server.Sync;

/// <summary>
/// Per-device bearer tokens. A token is 32 random bytes shown once at registration; only its SHA-256 is stored, and a
/// lookup is by hash, so comparing secrets in application code (and its timing) never happens.
/// </summary>
internal sealed class DeviceRegistry(NpgsqlDataSource db, IOptions<SyncOptions> options)
{
    public async Task<string> RegisterAsync(string nodeId, string storeId, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await SyncStore.EnsureStoreAsync(conn, tx, storeId, ct);
        // Re-registering a node rotates its token (a replaced tablet keeps its node id); the store cannot change,
        // because the device's existing changes are bound to it.
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO sync.devices (node_id, store_id, token_sha256) VALUES ($1, $2, $3)
            ON CONFLICT (node_id) DO UPDATE SET token_sha256 = excluded.token_sha256, revoked_at = NULL
            WHERE sync.devices.store_id = excluded.store_id
            """, conn, tx))
        {
            cmd.Parameters.Add(new() { Value = nodeId });
            cmd.Parameters.Add(new() { Value = storeId });
            cmd.Parameters.Add(new() { Value = Hash(token) });
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            {
                throw new InvalidOperationException($"Node '{nodeId}' is registered to another store.");
            }
        }

        await tx.CommitAsync(ct);
        return token;
    }

    public async Task<AuthenticatedDevice?> AuthenticateAsync(string? authorization, CancellationToken ct)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal) || authorization.Length > 200)
        {
            return null;
        }

        await using var cmd = db.CreateCommand("SELECT node_id, store_id FROM sync.devices WHERE token_sha256 = $1 AND revoked_at IS NULL");
        cmd.Parameters.Add(new() { Value = Hash(authorization[prefix.Length..].Trim()) });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new AuthenticatedDevice(reader.GetString(0), reader.GetString(1)) : null;
    }

    public bool IsAdmin(string? key)
    {
        var expected = options.Value.AdminKey;
        if (string.IsNullOrEmpty(expected) || key is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(expected));
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
