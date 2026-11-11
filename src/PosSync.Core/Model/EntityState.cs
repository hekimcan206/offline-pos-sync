namespace PosSync.Core.Model;

/// <summary>A last-writer-wins register: the current value of a field and the stamp of the write that set it.</summary>
public readonly record struct Register(string? Value, Stamp Stamp);

/// <summary>
/// The merged state of one entity on one replica. It is a pure function of the <em>set</em> of changes applied to
/// it (never of their order), which is the property the FsCheck suite checks for every merge strategy.
/// </summary>
public sealed class EntityState
{
    public EntityState(string storeId, string entity, Guid entityId)
    {
        StoreId = storeId;
        Entity = entity;
        EntityId = entityId;
    }

    public string StoreId { get; }
    public string Entity { get; }
    public Guid EntityId { get; }

    /// <summary>Register fields, keyed by field name (ordinal order keeps serialisation canonical).</summary>
    public SortedDictionary<string, Register> Registers { get; } = new(StringComparer.Ordinal);

    /// <summary>Counter fields: the sum of every applied delta.</summary>
    public SortedDictionary<string, long> Counters { get; } = new(StringComparer.Ordinal);

    /// <summary>Row-level LWW only: the stamp of the change whose row image is current.</summary>
    public Stamp? RowVersion { get; set; }

    public string? Get(string field) => Registers.TryGetValue(field, out var r) ? r.Value : null;

    public long Count(string field) => Counters.TryGetValue(field, out var c) ? c : 0;

    public Stamp? StampOf(string field) => Registers.TryGetValue(field, out var r) ? r.Stamp : null;
}
