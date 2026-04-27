namespace Ledjon.ReplicatedMemoryCache;

[GenerateSerializer]
[Alias("Ledjon.ReplicatedMemoryCache.VersionTag")]
internal readonly record struct VersionTag(long Ticks, int NodeId) : IComparable<VersionTag>
{
    public int CompareTo(VersionTag other)
    {
        var cmp = Ticks.CompareTo(other.Ticks);
        
        // If two nodes generated the exact same ticks, the NodeId ensures the
        // entire cluster deterministically agrees on the winner.

        return cmp != 0 ? cmp : NodeId.CompareTo(other.NodeId);
    }

    public static bool operator <(VersionTag left, VersionTag right) => left.CompareTo(right) < 0;
    public static bool operator >(VersionTag left, VersionTag right) => left.CompareTo(right) > 0;
    public static bool operator <=(VersionTag left, VersionTag right) => left.CompareTo(right) <= 0;
    public static bool operator >=(VersionTag left, VersionTag right) => left.CompareTo(right) >= 0;
}