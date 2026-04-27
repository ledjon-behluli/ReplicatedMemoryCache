using System.Runtime.CompilerServices;

namespace Ledjon.ReplicatedMemoryCache;

/// <summary>
/// A lock-free version of a that guarantees strictly monotonically increasing verions,
/// even if the physical clock drifts or goes backward.
/// </summary>
internal class VersionSource(int nodeId)
{
    private long _version;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VersionTag GetNextVersion(long physicalTicks)
    {
        long current;
        long next;

        do
        {
            current = Interlocked.Read(ref _version);

            // The new time must be strictly *greater* than the previous time.
            // If physical time is behind due to clock drift, we force it forward by (+1).

            next = Math.Max(physicalTicks, current + 1);
        }
        // So long as the current version is not equal to the value we read, we will retry the update.
        // This is because another thread may have updated the version in between our read and write.
        while (Interlocked.CompareExchange(ref _version, next, current) != current);

        return new VersionTag(next, nodeId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CatchUp(long incomingTicks)
    {
        long current;

        // If an incoming gossip mutation is from the "future", we advance our version!
        while (incomingTicks > (current = Interlocked.Read(ref _version)))
        {
            // CompareExchange returns the original value, so we loop until we successfully update the version, 
            // in case of concurrent updates from other threads.
            Interlocked.CompareExchange(ref _version, incomingTicks, current);
        }
    }
}