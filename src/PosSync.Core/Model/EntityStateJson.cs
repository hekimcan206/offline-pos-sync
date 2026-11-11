using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PosSync.Core.Clock;

namespace PosSync.Core.Model;

/// <summary>
/// Canonical JSON for <see cref="EntityState"/>: fixed property order and ordinal-sorted field names, so equal states
/// produce byte-identical documents. The device (SQLite TEXT) and the server (PostgreSQL jsonb) store this form, and
/// <see cref="Hash"/> over it is how the simulator, benchmarks and tests decide that replicas have converged.
/// </summary>
public static class EntityStateJson
{
    public static string Serialize(EntityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("store", state.StoreId);
            w.WriteString("entity", state.Entity);
            w.WriteString("eid", state.EntityId);
            w.WriteStartObject("r");
            foreach (var (field, register) in state.Registers)
            {
                w.WriteStartObject(field);
                w.WriteString("v", register.Value);
                w.WriteNumber("h", register.Stamp.Hlc.Pack());
                w.WriteString("n", register.Stamp.NodeId);
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteStartObject("c");
            foreach (var (field, value) in state.Counters)
            {
                w.WriteNumber(field, value);
            }

            w.WriteEndObject();
            if (state.RowVersion is { } rv)
            {
                w.WriteStartObject("rv");
                w.WriteNumber("h", rv.Hlc.Pack());
                w.WriteString("n", rv.NodeId);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static EntityState Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var state = new EntityState(
            root.GetProperty("store").GetString()!,
            root.GetProperty("entity").GetString()!,
            root.GetProperty("eid").GetGuid());

        foreach (var field in root.GetProperty("r").EnumerateObject())
        {
            state.Registers[field.Name] = new Register(
                field.Value.GetProperty("v").GetString(),
                ReadStamp(field.Value));
        }

        foreach (var counter in root.GetProperty("c").EnumerateObject())
        {
            state.Counters[counter.Name] = counter.Value.GetInt64();
        }

        if (root.TryGetProperty("rv", out var rv))
        {
            state.RowVersion = ReadStamp(rv);
        }

        return state;
    }

    /// <summary>SHA-256 over the canonical form of every state, in (entity, id) order. Equal hash = converged.</summary>
    public static string Hash(IEnumerable<EntityState> states)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var state in states.OrderBy(s => s.Entity, StringComparer.Ordinal).ThenBy(s => s.EntityId))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Serialize(state)));
            sha.AppendData("\n"u8);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    private static Stamp ReadStamp(JsonElement e) =>
        new(HlcTimestamp.Unpack(e.GetProperty("h").GetInt64()), e.GetProperty("n").GetString()!);
}
