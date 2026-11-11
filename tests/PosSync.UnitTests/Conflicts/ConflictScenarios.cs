using PosSync.Core.Model;
using PosSync.Core.Tickets;

namespace PosSync.UnitTests.Conflicts;

/// <summary>One row of the conflict table: what happened, which strategy merged it, what every replica ends with.</summary>
public sealed record ConflictScenario(string Id, string Input, MergeStrategy Strategy, string Outcome, Action<ScenarioWorld> Run)
{
    public override string ToString() => Id;
}

/// <summary>
/// The conflict catalogue. Each entry is executed by <see cref="ConflictScenarioTests"/> under two delivery orders and
/// rendered into docs/conflict-scenarios.md, so the table a reader sees is exactly the set of behaviours under test.
/// "Concurrent" means neither terminal had synced the other's edit when it made its own (both were offline).
/// </summary>
public static class ConflictScenarios
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<ConflictScenario> All { get; } =
    [
        new("S01",
            "T1 moves the ticket from table 12 to 14; concurrently T2 (later HLC) adds the note \"no onions\"",
            MergeStrategy.RowLww,
            "T2's row image wins: note kept, **table move lost** (still 12)",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                w.T1.At(1 * Second).Set(ticket, "table_no", "14");
                w.T2.At(2 * Second).Set(ticket, "note", "no onions");
                w.SyncAll();
                Each(w, r => r.Find(SyncSchema.Entities.Ticket, ticket)!.Get("table_no").Should().Be("12"));
                Each(w, r => r.Find(SyncSchema.Entities.Ticket, ticket)!.Get("note").Should().Be("no onions"));
            }),

        new("S02",
            "Same as S01",
            MergeStrategy.FieldLww,
            "Both edits kept: table 14 and note \"no onions\"",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                w.T1.At(1 * Second).Set(ticket, "table_no", "14");
                w.T2.At(2 * Second).Set(ticket, "note", "no onions");
                w.SyncAll();
                Each(w, r => r.Find(SyncSchema.Entities.Ticket, ticket)!.Get("table_no").Should().Be("14"));
                Each(w, r => r.Find(SyncSchema.Entities.Ticket, ticket)!.Get("note").Should().Be("no onions"));
            }),

        new("S03",
            "T1 sets note \"allergy: nuts\"; concurrently T2 (later HLC) sets note \"birthday\"",
            MergeStrategy.FieldLww,
            "Higher HLC wins on every replica: \"birthday\"",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                w.T1.At(1 * Second).Set(ticket, "note", "allergy: nuts");
                w.T2.At(2 * Second).Set(ticket, "note", "birthday");
                w.SyncAll();
                Each(w, r => r.Find(SyncSchema.Entities.Ticket, ticket)!.Get("note").Should().Be("birthday"));
            }),

        new("S04",
            "T1 and T2 set different notes with identical HLCs (same millisecond, same counter)",
            MergeStrategy.FieldLww,
            "Tie broken by node id (ordinal): T2's note wins everywhere, no replica disagrees",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                var a = w.T1.At(5 * Second).Set(ticket, "note", "from T1");
                var b = w.T2.At(5 * Second).Set(ticket, "note", "from T2");
                a.Hlc.Should().Be(b.Hlc);
                w.SyncAll();
                Each(w, r => r.Find(SyncSchema.Entities.Ticket, ticket)!.Get("note").Should().Be("from T2"));
            }),

        new("S05",
            "T2's wall clock is 10 min slow. T2 syncs, sees T1's note \"A\", then edits it to \"B\"",
            MergeStrategy.FieldLww,
            "\"B\" wins: the HLC carries causality, so the later edit has the larger stamp even from a slow clock (wall-clock LWW would keep \"A\")",
            w =>
            {
                w.T2.At(TimeSpan.FromMinutes(-10));
                var (ticket, _) = w.SharedTicket();
                var a = w.T1.At(1 * Second).Set(ticket, "note", "A");
                w.Sync(w.T1);
                w.Sync(w.T2);
                var b = w.T2.At(TimeSpan.FromMinutes(-10) + (2 * Second)).Set(ticket, "note", "B");
                w.SyncAll();
                w.T2.Time.Now.Should().BeBefore(w.T1.Time.Now, "T2's physical clock really is behind");
                b.Hlc.Should().BeGreaterThan(a.Hlc);
                Each(w, r => r.Find(SyncSchema.Entities.Ticket, ticket)!.Get("note").Should().Be("B"));
            }),

        new("S06",
            "The soup line has qty 1. T1 and T2 each tap \"+1\" concurrently",
            MergeStrategy.FieldLww,
            "qty = 2: both wrote the absolute value 2, **one increment lost**",
            w =>
            {
                var (_, line) = w.SharedTicket();
                w.T1.At(1 * Second).ChangeQty(line, +1);
                w.T2.At(2 * Second).ChangeQty(line, +1);
                w.SyncAll();
                Each(w, r => TicketOps.ReadQty(r.Find(SyncSchema.Entities.TicketLine, line)!).Should().Be(2));
            }),

        new("S07",
            "Same as S06",
            MergeStrategy.DomainRules,
            "qty = 3: quantity is a counter of uniquely identified deltas, both increments count",
            w =>
            {
                var (_, line) = w.SharedTicket();
                w.T1.At(1 * Second).ChangeQty(line, +1);
                w.T2.At(2 * Second).ChangeQty(line, +1);
                w.SyncAll();
                Each(w, r => TicketOps.ReadQty(r.Find(SyncSchema.Entities.TicketLine, line)!).Should().Be(3));
            }),

        new("S08",
            "T1 adds a lemonade and T2 adds a baklava to the same ticket concurrently",
            MergeStrategy.DomainRules,
            "Both lines kept (lines are separate entities: add-wins), total = 90.00 + 35.00 + 70.00",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                w.T1.At(1 * Second).AddLine(ticket, "DRINK-03", "Lemonade", 3_500, 1);
                w.T2.At(1 * Second).AddLine(ticket, "DESSERT-02", "Baklava", 7_000, 1);
                w.SyncAll();
                Each(w, r =>
                {
                    var view = w.Ticket(r, ticket);
                    view.Lines.Should().HaveCount(3);
                    view.TotalMinor.Should().Be(19_500);
                });
            }),

        new("S09",
            "T1 takes payment; concurrently T2 (later HLC) voids the ticket",
            MergeStrategy.FieldLww,
            "status = voided: **a paid sale silently disappears**",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                w.T1.At(1 * Second).Pay(ticket);
                w.T2.At(2 * Second).Void(ticket);
                w.SyncAll();
                Each(w, r => w.Ticket(r, ticket).Status.Should().Be(SyncSchema.TicketStatus.Voided));
            }),

        new("S10",
            "Same as S09",
            MergeStrategy.DomainRules,
            "status = paid (paid outranks voided whatever the clocks say); flag `void_overridden_by_payment` for the manager",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                w.T1.At(1 * Second).Pay(ticket);
                w.T2.At(2 * Second).Void(ticket);
                w.SyncAll();
                Each(w, r =>
                {
                    var view = w.Ticket(r, ticket);
                    view.Status.Should().Be(SyncSchema.TicketStatus.Paid);
                    view.Flags.Should().Contain(TicketProjection.VoidOverriddenByPayment);
                });
            }),

        new("S11",
            "T1 sends the ticket to the kitchen; T2, working from a stale copy, writes status \"open\" with a later HLC",
            MergeStrategy.DomainRules,
            "status stays \"sent\": the status is a forward-only state machine (max rank), a stale write cannot move it back",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                w.T1.At(1 * Second).SendToKitchen(ticket);
                w.T2.At(3 * Second).Set(ticket, "status", SyncSchema.TicketStatus.Open);
                w.SyncAll();
                Each(w, r => w.Ticket(r, ticket).Status.Should().Be(SyncSchema.TicketStatus.Sent));
            }),

        new("S12",
            "T1 takes payment; T2, offline, adds a dessert to the same ticket afterwards (later HLC)",
            MergeStrategy.DomainRules,
            "The dessert is **kept** and counted in the total, line marked added-after-close, ticket flagged `line_added_after_close`",
            w =>
            {
                var (ticket, _) = w.SharedTicket();
                w.T1.At(1 * Second).Pay(ticket);
                var dessert = w.T2.At(4 * Second).AddLine(ticket, "DESSERT-01", "Rice pudding", 6_000, 1);
                w.SyncAll();
                Each(w, r =>
                {
                    var view = w.Ticket(r, ticket);
                    view.Lines.Single(l => l.Id == dessert).AddedAfterClose.Should().BeTrue();
                    view.Flags.Should().Contain(TicketProjection.LineAddedAfterClose);
                    view.TotalMinor.Should().Be(15_000);
                });
            }),

        new("S13",
            "T1 voids the soup line; concurrently T2 taps \"+1\" on it",
            MergeStrategy.DomainRules,
            "Line stays voided (void is forward-only) and is excluded from the total; the increment is recorded but has no effect",
            w =>
            {
                var (ticket, line) = w.SharedTicket();
                w.T1.At(1 * Second).VoidLine(line);
                w.T2.At(2 * Second).ChangeQty(line, +1);
                w.SyncAll();
                Each(w, r =>
                {
                    var view = w.Ticket(r, ticket);
                    view.Lines.Single().Voided.Should().BeTrue();
                    view.TotalMinor.Should().Be(0);
                });
            }),

        new("S14",
            "Both terminals are offline and each opens a new ticket for table 7",
            MergeStrategy.DomainRules,
            "Two tickets kept (never merged: they may hold different orders), both flagged `duplicate_open_table`",
            w =>
            {
                var a = w.T1.At(1 * Second).OpenTicket("7");
                var b = w.T2.At(1 * Second).OpenTicket("7");
                w.SyncAll();
                Each(w, r =>
                {
                    var tickets = w.Tickets(r).Where(t => t.TableNo == "7").ToList();
                    tickets.Select(t => t.Id).Should().BeEquivalentTo([a, b]);
                    tickets.Should().OnlyContain(t => t.Flags.Contains(TicketProjection.DuplicateOpenTable));
                });
            }),

        new("S15",
            "Head office raises the menu price of the kebab from 450.00 to 520.00 while T1 is offline and rings one up",
            MergeStrategy.DomainRules,
            "The line keeps the captured price 450.00 (prices are copied into the line, not referenced); the menu shows 520.00 everywhere",
            w =>
            {
                var menuId = w.NextId();
                w.ServerWrite(SyncSchema.Entities.MenuItem, menuId,
                    [FieldOp.Set("sku", "MAIN-07"), FieldOp.Set("name", "Adana kebab"), FieldOp.Set("price_minor", "45000")]);
                var ticket = w.T1.OpenTicket("3");
                w.SyncAll();
                w.ServerTime.Advance(1 * Second);
                w.ServerWrite(SyncSchema.Entities.MenuItem, menuId, [FieldOp.Set("price_minor", "52000")]);
                var localPrice = long.Parse(w.T1.Replica.Find(SyncSchema.Entities.MenuItem, menuId)!.Get("price_minor")!, System.Globalization.CultureInfo.InvariantCulture);
                w.T1.At(2 * Second).AddLine(ticket, "MAIN-07", "Adana kebab", localPrice, 1);
                w.SyncAll();
                Each(w, r =>
                {
                    w.Ticket(r, ticket).Lines.Single().UnitPriceMinor.Should().Be(45_000);
                    r.Find(SyncSchema.Entities.MenuItem, menuId)!.Get("price_minor").Should().Be("52000");
                });
            }),

        new("S16",
            "T1 taps \"+1\"; the server stores the push but the response is lost, so T1 pushes the same change again",
            MergeStrategy.DomainRules,
            "Applied exactly once (deduplicated by change id): qty = 2, not 3",
            w =>
            {
                var (_, line) = w.SharedTicket();
                w.T1.At(1 * Second).ChangeQty(line, +1);
                w.PushWithLostAck(w.T1);
                w.SyncAll();
                Each(w, r => TicketOps.ReadQty(r.Find(SyncSchema.Entities.TicketLine, line)!).Should().Be(2));
            }),

        new("S17",
            "Offline, T1 opens a ticket, adds a line, taps \"+1\" and takes payment; the server receives the four changes in reverse order",
            MergeStrategy.DomainRules,
            "Same state as in-order delivery: paid, qty 2, total 180.00 (merges are commutative)",
            w =>
            {
                var ticket = w.T1.OpenTicket("9");
                var line = w.T1.AddLine(ticket, "SOUP-01", "Lentil soup", 9_000, 1);
                w.T1.ChangeQty(line, +1);
                w.T1.Pay(ticket);
                var reversed = new Core.Merge.InMemoryReplica(w.Schema);
                foreach (var change in Enumerable.Reverse(w.T1.Outbox))
                {
                    reversed.Apply(change);
                }

                w.SyncAll();
                reversed.Hash().Should().Be(w.T1.Replica.Hash());
                Each(w, r =>
                {
                    var view = w.Ticket(r, ticket);
                    view.Status.Should().Be(SyncSchema.TicketStatus.Paid);
                    view.TotalMinor.Should().Be(18_000);
                });
            }),
    ];

    private static void Each(ScenarioWorld w, Action<Core.Merge.InMemoryReplica> assert)
    {
        foreach (var replica in w.Replicas)
        {
            assert(replica);
        }
    }

    /// <summary>The markdown document generated from the catalogue (golden-tested against the committed file).</summary>
    public static string RenderMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("""
            # Conflict scenarios

            <!-- Generated from tests/PosSync.UnitTests/Conflicts/ConflictScenarios.cs. Do not edit by hand:
                 run the unit tests with UPDATE_GOLDEN=1 to regenerate. A golden test fails if this file drifts. -->

            Every row is executed by `ConflictScenarioTests.Scenario_produces_the_documented_outcome` twice (changes
            delivered in order, and in reverse order) on three replicas (server, T1, T2). The test also asserts that all
            replicas end with byte-identical state. "Concurrent" means neither terminal had synced the other's edit.

            Strategies: **RowLww** = whole-row last writer wins; **FieldLww** = per-field last writer wins (field-level
            merge); **DomainRules** = field-level merge plus counters and forward-only state machines (the default for
            tickets and lines). Rows in bold show data loss a strategy causes, which is why tickets use DomainRules.

            | # | Input | Strategy | Outcome on every replica |
            |---|---|---|---|

            """);
        foreach (var s in All)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $"| {s.Id} | {s.Input.Replace("|", "\\|", StringComparison.Ordinal)} | {s.Strategy} | {s.Outcome.Replace("|", "\\|", StringComparison.Ordinal)} |\n");
        }

        return sb.ToString();
    }
}
