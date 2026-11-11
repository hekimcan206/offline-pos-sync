using PosSync.Core.Clock;
using PosSync.UnitTests.Support;

namespace PosSync.UnitTests.Clock;

public sealed class HlcTests
{
    private static long Ms(ManualTime t) => t.Now.ToUnixTimeMilliseconds();

    [Fact]
    public void Now_follows_physical_time_when_it_advances()
    {
        var time = new ManualTime();
        var clock = new HybridLogicalClock(time);

        var a = clock.Now();
        time.AdvanceMs(5);
        var b = clock.Now();

        a.Should().Be(new HlcTimestamp(Ms(time) - 5, 0));
        b.Should().Be(new HlcTimestamp(Ms(time), 0));
    }

    [Fact]
    public void Now_counts_events_within_one_millisecond()
    {
        var clock = new HybridLogicalClock(new ManualTime());

        var stamps = Enumerable.Range(0, 3).Select(_ => clock.Now()).ToList();

        stamps.Select(s => s.Counter).Should().Equal(0, 1, 2);
        stamps.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Now_keeps_increasing_when_the_wall_clock_steps_backwards()
    {
        var time = new ManualTime();
        var clock = new HybridLogicalClock(time);
        var before = clock.Now();

        time.Advance(TimeSpan.FromMinutes(-3)); // NTP step or a dead RTC battery
        var after = clock.Now();

        after.Should().BeGreaterThan(before);
        after.WallMs.Should().Be(before.WallMs, "the logical part holds until physical time catches up");
    }

    [Fact]
    public void Observe_returns_a_timestamp_greater_than_the_remote_one()
    {
        var time = new ManualTime();
        var clock = new HybridLogicalClock(time);
        var remote = new HlcTimestamp(Ms(time) + 2_000, 7); // a peer whose clock runs 2 s ahead

        var received = clock.Observe(remote);
        var next = clock.Now();

        received.Should().Be(new HlcTimestamp(remote.WallMs, 8));
        next.Should().BeGreaterThan(received);
    }

    [Fact]
    public void Observe_refuses_a_remote_clock_beyond_the_drift_bound()
    {
        var time = new ManualTime();
        var clock = new HybridLogicalClock(time, maxForwardDrift: TimeSpan.FromMinutes(5));
        var before = clock.Now();

        var act = () => clock.Observe(new HlcTimestamp(Ms(time) + (long)TimeSpan.FromMinutes(6).TotalMilliseconds, 0));

        act.Should().Throw<ClockDriftException>();
        clock.Current.Should().Be(before, "a refused timestamp must not move the clock");
        clock.IsWithinDrift(new HlcTimestamp(Ms(time) + 299_000, 0)).Should().BeTrue();
    }

    [Fact]
    public void Counter_overflow_advances_the_logical_millisecond()
    {
        var time = new ManualTime();
        var clock = new HybridLogicalClock(time, new HlcTimestamp(Ms(time), HlcTimestamp.MaxCounter));

        var next = clock.Now();

        next.Should().Be(new HlcTimestamp(Ms(time) + 1, 0));
    }

    [Fact]
    public void Clock_resumes_from_a_persisted_value_after_restart()
    {
        var time = new ManualTime();
        var persisted = new HybridLogicalClock(time).Now();

        time.Advance(TimeSpan.FromHours(-1)); // the terminal rebooted with a wrong clock
        var restarted = new HybridLogicalClock(time, persisted);

        restarted.Now().Should().BeGreaterThan(persisted);
    }

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1_762_862_400_000L, 0)]
    [InlineData(1_762_862_400_000L, 65_535)]
    public void Packing_round_trips(long wall, int counter)
    {
        var ts = new HlcTimestamp(wall, counter);
        HlcTimestamp.Unpack(ts.Pack()).Should().Be(ts);
    }

    [Fact]
    public void Packing_rejects_out_of_range_values()
    {
        var act = () => new HlcTimestamp(-1, 0).Pack();
        act.Should().Throw<InvalidOperationException>();
    }
}
