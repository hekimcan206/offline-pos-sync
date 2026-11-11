namespace PosSync.UnitTests.Support;

/// <summary>A wall clock the test moves by hand, including backwards (NTP step, dead RTC battery).</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public static readonly DateTimeOffset Epoch = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    public ManualTime() : this(Epoch)
    {
    }

    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;

    public void AdvanceMs(long ms) => Now += TimeSpan.FromMilliseconds(ms);
}
