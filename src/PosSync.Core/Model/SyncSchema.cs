using System.Collections.Frozen;

namespace PosSync.Core.Model;

/// <summary>How concurrent changes to one entity are merged. Picked per entity type in the <see cref="SyncSchema"/>.</summary>
public enum MergeStrategy
{
    /// <summary>Whole-row last writer wins: the change with the highest stamp replaces the entire row.</summary>
    RowLww,

    /// <summary>Field-level merge: every field is its own last-writer-wins register.</summary>
    FieldLww,

    /// <summary>Field-level merge plus per-field domain semantics: counters, monotonic state machines.</summary>
    DomainRules,
}

/// <summary>Domain semantics of a field under <see cref="MergeStrategy.DomainRules"/>.</summary>
public enum FieldKind
{
    /// <summary>Last writer wins by stamp.</summary>
    Register,

    /// <summary>A counter: operations add deltas, concurrent increments all count.</summary>
    Counter,

    /// <summary>
    /// A value from an ordered set; the highest rank wins regardless of timestamps (a join-semilattice). Used for
    /// state machines that only move forward, such as the status of a ticket.
    /// </summary>
    Ranked,
}

public sealed record FieldSpec(string Name, FieldKind Kind, int MaxLength = 120, bool IsInteger = false, IReadOnlyList<string>? Ranks = null)
{
    public int RankOf(string? value)
    {
        if (Ranks is null || value is null)
        {
            return -1;
        }

        for (var i = 0; i < Ranks.Count; i++)
        {
            if (string.Equals(Ranks[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

public sealed class EntitySpec
{
    public EntitySpec(string name, MergeStrategy strategy, bool deviceWritable, IEnumerable<FieldSpec> fields)
    {
        Name = name;
        Strategy = strategy;
        DeviceWritable = deviceWritable;
        Fields = fields.ToFrozenDictionary(f => f.Name, StringComparer.Ordinal);
    }

    public string Name { get; }
    public MergeStrategy Strategy { get; }

    /// <summary>False for reference data (the menu): only the server may author it; device pushes are rejected.</summary>
    public bool DeviceWritable { get; }

    public FrozenDictionary<string, FieldSpec> Fields { get; }

    public EntitySpec WithStrategy(MergeStrategy strategy) => new(Name, strategy, DeviceWritable, Fields.Values);
}

/// <summary>
/// The replicated schema: entity types, their fields and the merge strategy for each. Every replica must run the
/// same schema version, because convergence depends on everyone merging the same way; the server rejects pushes
/// that declare another version.
/// </summary>
public sealed class SyncSchema
{
    public const int CurrentVersion = 1;

    public static class Entities
    {
        public const string Ticket = "ticket";
        public const string TicketLine = "ticket_line";
        public const string MenuItem = "menu_item";
    }

    public static class TicketStatus
    {
        public const string Open = "open";
        public const string Sent = "sent";
        public const string Voided = "voided";
        public const string Paid = "paid";

        /// <summary>
        /// Rank order. Paid outranks voided on purpose: money was taken, so a concurrent void on another terminal
        /// must not silently erase the sale. The void is kept as <c>void_requested</c> and flagged for review.
        /// </summary>
        public static readonly IReadOnlyList<string> Ranks = [Open, Sent, Voided, Paid];
    }

    public static readonly IReadOnlyList<string> FlagRanks = ["false", "true"];

    private readonly FrozenDictionary<string, EntitySpec> _entities;

    public SyncSchema(int version, IEnumerable<EntitySpec> entities)
    {
        Version = version;
        _entities = entities.ToFrozenDictionary(e => e.Name, StringComparer.Ordinal);
    }

    public int Version { get; }

    public IEnumerable<EntitySpec> EntityTypes => _entities.Values;

    public static SyncSchema Default { get; } = new(CurrentVersion,
    [
        new EntitySpec(Entities.Ticket, MergeStrategy.DomainRules, deviceWritable: true,
        [
            new FieldSpec("table_no", FieldKind.Register, MaxLength: 16),
            new FieldSpec("guest_count", FieldKind.Register, MaxLength: 3, IsInteger: true),
            new FieldSpec("note", FieldKind.Register, MaxLength: 200),
            new FieldSpec("opened_by", FieldKind.Register, MaxLength: 64),
            new FieldSpec("status", FieldKind.Ranked, Ranks: TicketStatus.Ranks),
            new FieldSpec("void_requested", FieldKind.Ranked, Ranks: FlagRanks),
        ]),
        new EntitySpec(Entities.TicketLine, MergeStrategy.DomainRules, deviceWritable: true,
        [
            new FieldSpec("ticket_id", FieldKind.Register, MaxLength: 36),
            new FieldSpec("sku", FieldKind.Register, MaxLength: 32),
            new FieldSpec("name", FieldKind.Register, MaxLength: 80),
            // Captured when the line is rung up: a later menu price change never re-prices an order already taken.
            new FieldSpec("unit_price_minor", FieldKind.Register, MaxLength: 9, IsInteger: true),
            new FieldSpec("qty", FieldKind.Counter, MaxLength: 6, IsInteger: true),
            new FieldSpec("voided", FieldKind.Ranked, Ranks: FlagRanks),
            new FieldSpec("note", FieldKind.Register, MaxLength: 120),
        ]),
        new EntitySpec(Entities.MenuItem, MergeStrategy.FieldLww, deviceWritable: false,
        [
            new FieldSpec("sku", FieldKind.Register, MaxLength: 32),
            new FieldSpec("name", FieldKind.Register, MaxLength: 80),
            new FieldSpec("category", FieldKind.Register, MaxLength: 40),
            new FieldSpec("price_minor", FieldKind.Register, MaxLength: 9, IsInteger: true),
            new FieldSpec("available", FieldKind.Register, MaxLength: 5),
        ]),
    ]);

    public EntitySpec? Find(string entity) => _entities.GetValueOrDefault(entity);

    public EntitySpec Get(string entity) =>
        Find(entity) ?? throw new ArgumentException($"Unknown entity '{entity}'.", nameof(entity));

    /// <summary>A copy of this schema with one entity merged by another strategy (used to compare strategies).</summary>
    public SyncSchema With(string entity, MergeStrategy strategy) =>
        new(Version, _entities.Values.Select(e => e.Name == entity ? e.WithStrategy(strategy) : e));
}
