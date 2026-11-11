using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using PosSync.Core.Clock;
using PosSync.UnitTests.Support;

namespace PosSync.UnitTests.Clock;

/// <summary>
/// The HLC guarantees from Kulkarni et al., checked over arbitrary event sequences in which the physical clock jumps
/// forwards and backwards and remote timestamps arrive from peers whose clocks are ahead or behind.
/// </summary>
public sealed class HlcPropertyTests
{
    /// <summary>One step of a node's life.</summary>
    public sealed record ClockEvent(int PhysicalJumpMs, bool Receive, int RemoteOffsetMs, int RemoteCounter);

    private static Gen<ClockEvent> EventGen =>
        from jump in Gen.Frequency(
            (6, Gen.Choose(0, 3)),           // usual: same or next millisecond
            (2, Gen.Choose(4, 5_000)),        // pauses
            (1, Gen.Choose(-120_000, -1)))    // wall clock steps backwards
        from receive in Gen.Elements(true, false)
        from offset in Gen.Choose(-60_000, 240_000) // peers up to 4 min ahead (inside the 5 min drift bound)
        from counter in Gen.Choose(0, 50)
        select new ClockEvent(jump, receive, offset, counter);

    public static Arbitrary<ClockEvent[]> Steps() =>
        Gen.ArrayOf(EventGen).Where(s => s.Length is > 0 and <= 200).ToArbitrary();

    [Property(Arbitrary = [typeof(HlcPropertyTests)], MaxTest = 500)]
    public bool Timestamps_are_strictly_increasing(ClockEvent[] steps)
    {
        var time = new ManualTime();
        var clock = new HybridLogicalClock(time);
        var previous = HlcTimestamp.Zero;
        foreach (var step in steps)
        {
            time.AdvanceMs(step.PhysicalJumpMs);
            var next = step.Receive ? clock.Observe(Remote(time, step)) : clock.Now();
            if (next <= previous)
            {
                return false;
            }

            previous = next;
        }

        return true;
    }

    [Property(Arbitrary = [typeof(HlcPropertyTests)], MaxTest = 500)]
    public bool A_receive_event_is_later_than_the_message_received(ClockEvent[] steps)
    {
        var time = new ManualTime();
        var clock = new HybridLogicalClock(time);
        foreach (var step in steps.Where(s => s.Receive))
        {
            time.AdvanceMs(step.PhysicalJumpMs);
            var remote = Remote(time, step);
            if (clock.Observe(remote) <= remote)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Bounded divergence: the logical wall time never exceeds the largest physical time or remote wall time the node
    /// has seen (plus one millisecond per counter overflow, which these sequences are too short to trigger).
    /// </summary>
    [Property(Arbitrary = [typeof(HlcPropertyTests)], MaxTest = 500)]
    public bool Wall_component_stays_within_what_the_node_has_seen(ClockEvent[] steps)
    {
        var time = new ManualTime();
        var clock = new HybridLogicalClock(time);
        long maxSeen = 0;
        foreach (var step in steps)
        {
            time.AdvanceMs(step.PhysicalJumpMs);
            maxSeen = Math.Max(maxSeen, time.Now.ToUnixTimeMilliseconds());
            HlcTimestamp ts;
            if (step.Receive)
            {
                var remote = Remote(time, step);
                maxSeen = Math.Max(maxSeen, remote.WallMs);
                ts = clock.Observe(remote);
            }
            else
            {
                ts = clock.Now();
            }

            if (ts.WallMs > maxSeen)
            {
                return false;
            }
        }

        return true;
    }

    [Property(MaxTest = 1000)]
    public bool Packed_order_equals_timestamp_order(NonNegativeInt wallA, byte counterA, NonNegativeInt wallB, byte counterB)
    {
        var a = new HlcTimestamp(1_700_000_000_000L + wallA.Get, counterA);
        var b = new HlcTimestamp(1_700_000_000_000L + wallB.Get, counterB);
        return Math.Sign(a.CompareTo(b)) == Math.Sign(a.Pack().CompareTo(b.Pack()));
    }

    private static HlcTimestamp Remote(ManualTime time, ClockEvent step) =>
        new(Math.Max(0, time.Now.ToUnixTimeMilliseconds() + step.RemoteOffsetMs), step.RemoteCounter);
}
