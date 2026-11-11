using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using PosSync.Core.Model;
using PosSync.Device;
using PosSync.UnitTests.Support;

namespace PosSync.UnitTests.Device;

/// <summary>
/// End-to-end convergence of the real device stack (SQLite store, outbox, sync client, merge) under arbitrary
/// interleavings of cashier actions, sync rounds, link failures, lost acknowledgements and skewed clocks.
/// After the network heals and every terminal syncs, three things must hold:
/// <list type="number">
/// <item>every terminal and the server have byte-identical state (convergence);</item>
/// <item>every change any terminal ever wrote is on the server (no lost ticket);</item>
/// <item>the server stored each change exactly once (idempotent replay).</item>
/// </list>
/// </summary>
public sealed class ProtocolConvergencePropertyTests
{
    private const string Store = "store-001";
    private static readonly MenuEntry[] Menu =
    [
        new(Guid.NewGuid(), "SOUP-01", "Lentil soup", "Starters", 9_000, true),
        new(Guid.NewGuid(), "MAIN-07", "Adana kebab", "Mains", 45_000, true),
        new(Guid.NewGuid(), "DRINK-03", "Lemonade", "Drinks", 3_500, true),
    ];

    public enum Kind
    {
        OpenTicket,
        AddLine,
        ChangeQty,
        Status,
        Note,
        Sync,
        ToggleLink,
        LostAckSync,
        Tick,
    }

    public sealed record Act(Kind Kind, int Device, int A, int B);

    public sealed record Schedule(int[] ClockOffsetsMs, Act[] Acts);

    public static Arbitrary<Schedule> Schedules() =>
        (from offsets in Gen.ArrayOf(Gen.Choose(-90_000, 90_000), 3)
         from acts in Gen.ArrayOf(
                 from kind in Gen.Frequency(
                     (3, Gen.Constant(Kind.OpenTicket)), (5, Gen.Constant(Kind.AddLine)), (4, Gen.Constant(Kind.ChangeQty)),
                     (2, Gen.Constant(Kind.Status)), (2, Gen.Constant(Kind.Note)), (4, Gen.Constant(Kind.Sync)),
                     (2, Gen.Constant(Kind.ToggleLink)), (1, Gen.Constant(Kind.LostAckSync)), (3, Gen.Constant(Kind.Tick)))
                 from device in Gen.Choose(0, 2)
                 from a in Gen.Choose(0, 1000)
                 from b in Gen.Choose(-2, 3)
                 select new Act(kind, device, a, b))
             .Where(a => a.Length is > 0 and <= 70)
         select new Schedule(offsets, acts)).ToArbitrary();

    [Property(Arbitrary = [typeof(ProtocolConvergencePropertyTests)], MaxTest = 60)]
    public async Task<bool> Terminals_converge_and_lose_nothing_under_any_schedule(Schedule schedule)
    {
        var hubTime = new ManualTime();
        var hub = new InMemoryHub(SyncSchema.Default, hubTime);
        var devices = new List<(PosTerminal Pos, HubTransport Link, SyncClient Sync, ManualTime Time)>();
        var written = new HashSet<Guid>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var identity = new DeviceIdentity($"T{i + 1}", Store);
                var time = new ManualTime(hubTime.Now + TimeSpan.FromMilliseconds(schedule.ClockOffsetsMs[i]));
                var store = await DeviceStore.OpenAsync(":memory:", identity, SyncSchema.Default, time);
                var link = new HubTransport(hub, identity);
                devices.Add((new PosTerminal(store), link, new SyncClient(store, link, new SyncClientOptions { PushBatchSize = 4, PullBatchSize = 5 }), time));
            }

            foreach (var act in schedule.Acts)
            {
                var d = devices[act.Device];
                await RunAsync(d.Pos, d.Link, d.Sync, d.Time, hubTime, act);
                foreach (var item in await d.Pos.Store.PeekOutboxAsync(1000))
                {
                    written.Add(item.Change.ChangeId);
                }
            }

            // Heal: every link up, two full rounds so the last terminal's changes reach the first one.
            foreach (var d in devices)
            {
                d.Link.Online = true;
            }

            for (var round = 0; round < 2; round++)
            {
                foreach (var d in devices)
                {
                    await d.Sync.SyncOnceAsync();
                }
            }

            var serverHash = hub.Replica(Store).Hash();
            foreach (var d in devices)
            {
                if (await d.Pos.Store.HashAsync() != serverHash || await d.Pos.Store.PendingCountAsync() != 0)
                {
                    return false;
                }
            }

            return written.All(hub.ContainsChange) && hub.StoredChanges == written.Count;
        }
        finally
        {
            foreach (var d in devices)
            {
                await d.Pos.Store.DisposeAsync();
            }
        }
    }

    private static async Task RunAsync(PosTerminal pos, HubTransport link, SyncClient sync, ManualTime time, ManualTime hubTime, Act act)
    {
        var tickets = (await pos.Store.AllStatesAsync(SyncSchema.Entities.Ticket)).Select(s => s.EntityId).OrderBy(g => g).ToList();
        var lines = (await pos.Store.AllStatesAsync(SyncSchema.Entities.TicketLine)).Select(s => s.EntityId).OrderBy(g => g).ToList();
        switch (act.Kind)
        {
            case Kind.OpenTicket:
                await pos.OpenTicketAsync((act.A % 20).ToString(System.Globalization.CultureInfo.InvariantCulture), 2);
                break;
            case Kind.AddLine when tickets.Count > 0:
                await pos.AddLineAsync(tickets[act.A % tickets.Count], Menu[act.A % Menu.Length], 1 + (act.A % 3));
                break;
            case Kind.ChangeQty when lines.Count > 0:
                await pos.ChangeQtyAsync(lines[act.A % lines.Count], act.B == 0 ? 1 : act.B);
                break;
            case Kind.Status when tickets.Count > 0:
                var t = tickets[act.A % tickets.Count];
                await (act.B switch
                {
                    < 0 => pos.VoidAsync(t),
                    0 => pos.SendToKitchenAsync(t),
                    1 => pos.PayAsync(t),
                    _ => lines.Count > 0 ? pos.VoidLineAsync(lines[act.A % lines.Count]) : pos.SendToKitchenAsync(t),
                });
                break;
            case Kind.Note when tickets.Count > 0:
                await pos.SetNoteAsync(tickets[act.A % tickets.Count], $"note {act.A}");
                break;
            case Kind.Sync:
                await TrySync(sync);
                break;
            case Kind.ToggleLink:
                link.Online = !link.Online;
                break;
            case Kind.LostAckSync:
                link.DropNextPushResponse = true;
                await TrySync(sync);
                link.DropNextPushResponse = false;
                break;
            case Kind.Tick:
                time.AdvanceMs(act.A);
                hubTime.AdvanceMs(act.A);
                break;
            default:
                break; // an action with nothing to act on yet
        }
    }

    private static async Task TrySync(SyncClient sync)
    {
        try
        {
            await sync.SyncOnceAsync();
        }
        catch (HttpRequestException)
        {
            // Link down or response lost: exactly the situations the outbox exists for.
        }
    }
}
