using PosSync.Core.Clock;

namespace PosSync.Core.Model;

/// <summary>
/// The total order every replica uses to decide "which write is later": HLC first, then the node id (ordinal).
/// Two different nodes can issue the same HLC; the node id makes the order total, so every replica picks the same
/// winner without coordination.
/// </summary>
public readonly record struct Stamp(HlcTimestamp Hlc, string NodeId) : IComparable<Stamp>
{
    public int CompareTo(Stamp other)
    {
        var hlc = Hlc.CompareTo(other.Hlc);
        return hlc != 0 ? hlc : string.CompareOrdinal(NodeId, other.NodeId);
    }

    public static bool operator <(Stamp left, Stamp right) => left.CompareTo(right) < 0;
    public static bool operator >(Stamp left, Stamp right) => left.CompareTo(right) > 0;
    public static bool operator <=(Stamp left, Stamp right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Stamp left, Stamp right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Hlc}@{NodeId}";
}
