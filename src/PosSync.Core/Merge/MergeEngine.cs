using System.Globalization;
using PosSync.Core.Model;

namespace PosSync.Core.Merge;

/// <summary>
/// Applies one change to an entity state. This is the only code that decides conflicts: the SQLite device store, the
/// PostgreSQL server store and the in-memory replica used by the property tests all call it.
/// </summary>
/// <remarks>
/// Every rule here is a join on a semilattice (max by stamp, max by rank, sum of uniquely identified deltas), so
/// applying the same set of changes in any order yields the same state. Exactly-once application of each change id
/// is the caller's job (replicas keep the set of applied ids); it is what makes the counter sum safe.
/// </remarks>
public static class MergeEngine
{
    public static void Apply(EntitySpec spec, EntityState state, Change change)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(change);
        if (!string.Equals(state.Entity, change.Entity, StringComparison.Ordinal) || state.EntityId != change.EntityId)
        {
            throw new ArgumentException("Change does not belong to this entity.", nameof(change));
        }

        var stamp = change.Stamp;
        switch (spec.Strategy)
        {
            case MergeStrategy.RowLww:
                ApplyRow(state, change, stamp);
                break;
            case MergeStrategy.FieldLww:
                foreach (var op in change.Ops)
                {
                    SetIfNewer(state, op, stamp);
                }

                break;
            case MergeStrategy.DomainRules:
                foreach (var op in change.Ops)
                {
                    ApplyDomain(spec.Fields[op.Field], state, op, stamp);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec), spec.Strategy, "Unknown merge strategy.");
        }
    }

    private static void ApplyRow(EntityState state, Change change, Stamp stamp)
    {
        // The row image of the newest change wins outright, including fields it does not mention: that is what
        // whole-row LWW means, and why it loses concurrent edits to different fields.
        if (state.RowVersion is { } current && current >= stamp)
        {
            return;
        }

        state.Registers.Clear();
        state.Counters.Clear();
        foreach (var op in change.Ops)
        {
            state.Registers[op.Field] = new Register(op.Value, stamp);
        }

        state.RowVersion = stamp;
    }

    private static void SetIfNewer(EntityState state, FieldOp op, Stamp stamp)
    {
        if (!state.Registers.TryGetValue(op.Field, out var current) || stamp > current.Stamp)
        {
            state.Registers[op.Field] = new Register(op.Value, stamp);
        }
    }

    private static void ApplyDomain(FieldSpec field, EntityState state, FieldOp op, Stamp stamp)
    {
        switch (field.Kind)
        {
            case FieldKind.Register:
                SetIfNewer(state, op, stamp);
                break;

            case FieldKind.Counter:
                var delta = long.Parse(op.Value!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                state.Counters[op.Field] = state.Count(op.Field) + delta;
                break;

            case FieldKind.Ranked:
                if (!state.Registers.TryGetValue(op.Field, out var current))
                {
                    state.Registers[op.Field] = new Register(op.Value, stamp);
                    break;
                }

                var incoming = field.RankOf(op.Value);
                var existing = field.RankOf(current.Value);
                // Higher rank wins whatever the clocks say. For an equal rank the earliest stamp is kept: it records
                // when the state was first reached (when the ticket was first paid), and min is order-independent.
                if (incoming > existing || (incoming == existing && stamp < current.Stamp))
                {
                    state.Registers[op.Field] = new Register(op.Value, stamp);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(field), field.Kind, "Unknown field kind.");
        }
    }
}
