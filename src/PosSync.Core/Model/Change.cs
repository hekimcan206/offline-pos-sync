using System.Text.Json.Serialization;
using PosSync.Core.Clock;

namespace PosSync.Core.Model;

/// <summary>What a field operation does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OpKind>))]
public enum OpKind
{
    /// <summary>Assign a value (register semantics).</summary>
    [JsonStringEnumMemberName("set")] Set,

    /// <summary>Add a signed delta to a counter field (only under <see cref="MergeStrategy.DomainRules"/>).</summary>
    [JsonStringEnumMemberName("add")] Add,
}

/// <summary>One field assignment inside a change. Values travel as strings; the schema says how to read them.</summary>
public sealed record FieldOp(
    [property: JsonPropertyName("f")] string Field,
    [property: JsonPropertyName("k")] OpKind Kind,
    [property: JsonPropertyName("v")] string? Value)
{
    public static FieldOp Set(string field, string? value) => new(field, OpKind.Set, value);

    public static FieldOp Add(string field, long delta) =>
        new(field, OpKind.Add, delta.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// The unit of replication: an immutable, uniquely identified set of field operations on one entity, stamped with
/// the issuing node's HLC. The id is generated on the device when the change is written to its outbox, which is what
/// makes every retry of every push idempotent: the server and other devices apply a given id at most once.
/// </summary>
public sealed record Change(
    [property: JsonPropertyName("id")] Guid ChangeId,
    [property: JsonPropertyName("store")] string StoreId,
    [property: JsonPropertyName("entity")] string Entity,
    [property: JsonPropertyName("eid")] Guid EntityId,
    [property: JsonPropertyName("hlc"), JsonConverter(typeof(HlcJsonConverter))] HlcTimestamp Hlc,
    [property: JsonPropertyName("node")] string NodeId,
    [property: JsonPropertyName("ops")] IReadOnlyList<FieldOp> Ops)
{
    [JsonIgnore]
    public Stamp Stamp => new(Hlc, NodeId);
}
