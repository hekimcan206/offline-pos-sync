using System.Globalization;

namespace PosSync.Core.Clock;

/// <summary>
/// A hybrid logical clock timestamp (Kulkarni et al., OPODIS 2014): <see cref="WallMs"/> is the largest physical
/// time (Unix milliseconds) the issuing node has seen, <see cref="Counter"/> orders events that share it.
/// </summary>
/// <remarks>
/// Packs into one sortable 64-bit integer (48 bits of milliseconds, 16 bits of counter), which is what goes over
/// the wire and into both databases: comparing packed values is the same as comparing the pair.
/// 2^48 ms reaches the year 10889, so the wall part never overflows in practice.
/// </remarks>
public readonly record struct HlcTimestamp(long WallMs, int Counter) : IComparable<HlcTimestamp>
{
    public const int MaxCounter = ushort.MaxValue;
    private const long MaxWallMs = (1L << 47) - 1;

    public static HlcTimestamp Zero { get; } = new(0, 0);

    public long Pack()
    {
        if (WallMs is < 0 or > MaxWallMs || Counter is < 0 or > MaxCounter)
        {
            throw new InvalidOperationException($"HLC {this} is outside the packable range.");
        }

        return (WallMs << 16) | (uint)Counter;
    }

    public static HlcTimestamp Unpack(long packed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packed);
        return new HlcTimestamp(packed >> 16, (int)(packed & 0xFFFF));
    }

    public int CompareTo(HlcTimestamp other)
    {
        var wall = WallMs.CompareTo(other.WallMs);
        return wall != 0 ? wall : Counter.CompareTo(other.Counter);
    }

    public static bool operator <(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) < 0;
    public static bool operator >(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) > 0;
    public static bool operator <=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) <= 0;
    public static bool operator >=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) >= 0;

    public static HlcTimestamp Max(HlcTimestamp a, HlcTimestamp b) => a >= b ? a : b;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{WallMs:D13}.{Counter:D5}");
}
