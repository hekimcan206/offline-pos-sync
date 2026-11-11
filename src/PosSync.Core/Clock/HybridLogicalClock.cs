namespace PosSync.Core.Clock;

/// <summary>
/// Hybrid logical clock. Timestamps it issues are strictly increasing on this node, are greater than every remote
/// timestamp it has observed (so "happened before" implies "smaller"), and stay close to physical time.
/// </summary>
/// <remarks>
/// Algorithm from Kulkarni, Demirbas et al., "Logical Physical Clocks" (OPODIS 2014), with two production
/// guards the paper leaves to the implementer:
/// <list type="bullet">
/// <item>A remote timestamp further ahead of local physical time than <see cref="MaxForwardDrift"/> is refused
/// (<see cref="ClockDriftException"/>). Without it, one terminal whose clock says 2099 would drag every node's
/// clock to 2099 and win every last-writer-wins comparison forever.</item>
/// <item>When the 16-bit counter would overflow, the logical wall time advances by one millisecond instead.
/// That needs 65 536 events inside one millisecond with a stalled physical clock; the cost is that the clock runs
/// up to a few ms ahead of physical time, which the paper's bound already allows for.</item>
/// </list>
/// The clock is thread-safe. Persist <see cref="Current"/> with every change you write and pass it back as the
/// initial value after a restart, so a wall clock that jumped backwards while the device was off cannot make the
/// node reissue old timestamps.
/// </remarks>
public sealed class HybridLogicalClock
{
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private HlcTimestamp _last;

    public HybridLogicalClock(TimeProvider time, HlcTimestamp initial = default, TimeSpan? maxForwardDrift = null)
    {
        _time = time;
        _last = initial;
        MaxForwardDrift = maxForwardDrift ?? TimeSpan.FromMinutes(5);
    }

    public TimeSpan MaxForwardDrift { get; }

    /// <summary>The last timestamp issued or observed.</summary>
    public HlcTimestamp Current
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>Timestamp for a local event or a message about to be sent.</summary>
    public HlcTimestamp Now()
    {
        lock (_gate)
        {
            var physical = PhysicalMs();
            _last = physical > _last.WallMs
                ? new HlcTimestamp(physical, 0)
                : Increment(_last.WallMs, _last.Counter);
            return _last;
        }
    }

    /// <summary>Merge a timestamp received from another node; returns the timestamp of the receive event.</summary>
    /// <exception cref="ClockDriftException">The remote clock is implausibly far ahead of ours.</exception>
    public HlcTimestamp Observe(HlcTimestamp remote)
    {
        lock (_gate)
        {
            var physical = PhysicalMs();
            if (remote.WallMs - physical > (long)MaxForwardDrift.TotalMilliseconds)
            {
                throw new ClockDriftException(remote, physical, MaxForwardDrift);
            }

            var wall = Math.Max(Math.Max(_last.WallMs, remote.WallMs), physical);
            if (wall == _last.WallMs && wall == remote.WallMs)
            {
                _last = Increment(wall, Math.Max(_last.Counter, remote.Counter));
            }
            else if (wall == _last.WallMs)
            {
                _last = Increment(wall, _last.Counter);
            }
            else if (wall == remote.WallMs)
            {
                _last = Increment(wall, remote.Counter);
            }
            else
            {
                _last = new HlcTimestamp(wall, 0);
            }

            return _last;
        }
    }

    /// <summary>True when <paramref name="remote"/> would be accepted by <see cref="Observe"/>.</summary>
    public bool IsWithinDrift(HlcTimestamp remote) =>
        remote.WallMs - PhysicalMs() <= (long)MaxForwardDrift.TotalMilliseconds;

    private long PhysicalMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private static HlcTimestamp Increment(long wall, int counter) =>
        counter >= HlcTimestamp.MaxCounter ? new HlcTimestamp(wall + 1, 0) : new HlcTimestamp(wall, counter + 1);
}

/// <summary>A remote timestamp was further ahead of local physical time than the configured maximum drift.</summary>
public sealed class ClockDriftException(HlcTimestamp remote, long localPhysicalMs, TimeSpan maxDrift)
    : Exception($"Remote HLC {remote} is {remote.WallMs - localPhysicalMs} ms ahead of local time (max {maxDrift.TotalMilliseconds} ms).")
{
    public HlcTimestamp Remote { get; } = remote;
    public long LocalPhysicalMs { get; } = localPhysicalMs;
}
