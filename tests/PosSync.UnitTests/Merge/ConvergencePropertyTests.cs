using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using PosSync.Core.Clock;
using PosSync.Core.Merge;
using PosSync.Core.Model;

namespace PosSync.UnitTests.Merge;

/// <summary>
/// Strong eventual consistency of the merge layer: replicas that apply the same set of changes reach the same state,
/// whatever the order and however many times each change is delivered. Checked for every strategy, with stamps chosen
/// so that cross-node HLC ties (same wall time and counter) occur often and the node-id tie-break is exercised.
/// </summary>
public sealed class ConvergencePropertyTests
{
    private const string Store = "store-1";
    private static readonly string[] Nodes = ["T1", "T2", "T3"];
    private static readonly Guid[] TicketIds = [Id(1), Id(2)];
    private static readonly Guid[] LineIds = [Id(11), Id(12), Id(13)];

    /// <summary>An operation before stamping: which node, which entity, what it does.</summary>
    public sealed record Draft(int Node, bool OnLine, int Entity, int Op, int Arg, int WallOffset);

    public sealed record Scenario(MergeStrategy Strategy, Change[] Changes, int[] OrderA, int[] OrderB, int[] Duplicates);

    private static Gen<Draft> DraftGen =>
        from node in Gen.Choose(0, Nodes.Length - 1)
        from onLine in Gen.Elements(true, false)
        from entity in Gen.Choose(0, 2)
        from op in Gen.Choose(0, 5)
        from arg in Gen.Choose(-3, 9)
        from wall in Gen.Choose(0, 4) // a narrow window: many equal wall times across nodes
        select new Draft(node, onLine, entity, op, arg, wall);

    public static Arbitrary<Scenario> Scenarios() =>
        (from strategy in Gen.Elements(MergeStrategy.RowLww, MergeStrategy.FieldLww, MergeStrategy.DomainRules)
         from drafts in Gen.ArrayOf(DraftGen).Where(d => d.Length is > 0 and <= 60)
         let changes = Stamp(strategy, drafts)
         from orderA in Shuffle(changes.Length)
         from orderB in Shuffle(changes.Length)
         from dups in Gen.ArrayOf(Gen.Choose(0, changes.Length - 1)).Where(d => d.Length <= 20)
         select new Scenario(strategy, changes, orderA, orderB, dups)).ToArbitrary();

    [Property(Arbitrary = [typeof(ConvergencePropertyTests)], MaxTest = 400)]
    public bool Replicas_converge_under_any_delivery_order_and_duplication(Scenario s)
    {
        var schema = SchemaFor(s.Strategy);
        var a = new InMemoryReplica(schema);
        var b = new InMemoryReplica(schema);

        foreach (var i in s.OrderA)
        {
            a.Apply(s.Changes[i]);
        }

        // Replica B also receives duplicates, interleaved before the in-order pass (retries after lost acks).
        foreach (var i in s.Duplicates)
        {
            b.Apply(s.Changes[i]);
        }

        foreach (var i in s.OrderB)
        {
            b.Apply(s.Changes[i]);
        }

        foreach (var i in s.Duplicates)
        {
            b.Apply(s.Changes[i]);
        }

        return a.Hash() == b.Hash() && a.AppliedCount == s.Changes.Length && b.AppliedCount == s.Changes.Length;
    }

    [Property(Arbitrary = [typeof(ConvergencePropertyTests)], MaxTest = 200)]
    public bool Merge_is_a_function_of_the_change_set(Scenario s)
    {
        // Applying a prefix, snapshotting through canonical JSON and continuing must equal applying everything at once:
        // persisted state carries all the information the merge needs (nothing lives only in memory).
        var schema = SchemaFor(s.Strategy);
        var direct = new InMemoryReplica(schema);
        foreach (var i in s.OrderA)
        {
            direct.Apply(s.Changes[i]);
        }

        var states = new Dictionary<(string, Guid), EntityState>();
        foreach (var i in s.OrderB)
        {
            var c = s.Changes[i];
            var state = states.TryGetValue((c.Entity, c.EntityId), out var st)
                ? EntityStateJson.Deserialize(EntityStateJson.Serialize(st))
                : new EntityState(c.StoreId, c.Entity, c.EntityId);
            MergeEngine.Apply(schema.Get(c.Entity), state, c);
            states[(c.Entity, c.EntityId)] = state;
        }

        return EntityStateJson.Hash(states.Values) == direct.Hash();
    }

    internal static SyncSchema SchemaFor(MergeStrategy strategy) =>
        SyncSchema.Default
            .With(SyncSchema.Entities.Ticket, strategy)
            .With(SyncSchema.Entities.TicketLine, strategy);

    /// <summary>
    /// Assigns HLCs the way real nodes would: per node strictly increasing (a node never reuses a timestamp), while
    /// different nodes freely collide on the same (wall, counter).
    /// </summary>
    private static Change[] Stamp(MergeStrategy strategy, Draft[] drafts)
    {
        var schema = SchemaFor(strategy);
        var perNode = new int[Nodes.Length];
        var result = new Change[drafts.Length];
        for (var i = 0; i < drafts.Length; i++)
        {
            var d = drafts[i];
            var seq = perNode[d.Node]++;
            var hlc = new HlcTimestamp(1_762_862_400_000L + (seq * 10) + d.WallOffset, 0);
            var (entity, id, ops) = Ops(schema, d);
            result[i] = new Change(Guid.NewGuid(), Store, entity, id, hlc, Nodes[d.Node], ops);
            ChangeValidator.Validate(schema, result[i], fromDevice: true).Should().BeNull();
        }

        return result;
    }

    private static (string Entity, Guid Id, IReadOnlyList<FieldOp> Ops) Ops(SyncSchema schema, Draft d)
    {
        var strategy = schema.Get(SyncSchema.Entities.Ticket).Strategy;
        if (d.OnLine)
        {
            var id = LineIds[d.Entity % LineIds.Length];
            IReadOnlyList<FieldOp> ops = (d.Op % 3) switch
            {
                0 => strategy == MergeStrategy.DomainRules ? [FieldOp.Add("qty", d.Arg)] : [FieldOp.Set("qty", d.Arg.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                1 => [FieldOp.Set("voided", d.Arg > 4 ? "true" : "false")],
                _ => [FieldOp.Set("note", $"n{d.Arg}"), FieldOp.Set("ticket_id", TicketIds[0].ToString())],
            };
            return (SyncSchema.Entities.TicketLine, id, ops);
        }

        var ticket = TicketIds[d.Entity % TicketIds.Length];
        IReadOnlyList<FieldOp> ticketOps = d.Op switch
        {
            0 => [FieldOp.Set("note", $"note {d.Arg}")],
            1 => [FieldOp.Set("table_no", $"T{d.Arg}")],
            2 => [FieldOp.Set("status", SyncSchema.TicketStatus.Ranks[Math.Abs(d.Arg) % 4])],
            3 => [FieldOp.Set("void_requested", d.Arg % 2 == 0 ? "true" : "false")],
            4 => [FieldOp.Set("note", null)],
            _ => [FieldOp.Set("table_no", $"T{d.Arg}"), FieldOp.Set("guest_count", "2"), FieldOp.Set("status", SyncSchema.TicketStatus.Open)],
        };
        return (SyncSchema.Entities.Ticket, ticket, ticketOps);
    }

    private static Gen<int[]> Shuffle(int n) => Gen.Shuffle(Enumerable.Range(0, n).ToArray());

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");
}
