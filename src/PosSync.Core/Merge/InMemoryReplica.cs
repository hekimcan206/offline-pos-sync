using PosSync.Core.Model;

namespace PosSync.Core.Merge;

/// <summary>
/// A replica held in memory: the set of applied change ids plus merged entity states. It is the reference model the
/// property tests run against, and the shape both persistent stores follow (applied-id set + state table).
/// </summary>
public sealed class InMemoryReplica(SyncSchema schema)
{
    private readonly HashSet<Guid> _applied = [];
    private readonly Dictionary<(string Entity, Guid Id), EntityState> _states = [];

    public SyncSchema Schema { get; } = schema;

    public int AppliedCount => _applied.Count;

    public IEnumerable<EntityState> States => _states.Values;

    /// <summary>Applies the change unless its id was applied before. Returns false for a duplicate.</summary>
    public bool Apply(Change change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!_applied.Add(change.ChangeId))
        {
            return false;
        }

        var key = (change.Entity, change.EntityId);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new EntityState(change.StoreId, change.Entity, change.EntityId);
            _states[key] = state;
        }

        MergeEngine.Apply(Schema.Get(change.Entity), state, change);
        return true;
    }

    public EntityState? Find(string entity, Guid id) => _states.GetValueOrDefault((entity, id));

    public string Hash() => EntityStateJson.Hash(_states.Values);
}
