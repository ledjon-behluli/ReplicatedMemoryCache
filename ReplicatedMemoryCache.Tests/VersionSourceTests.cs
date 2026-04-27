using CsCheck;

namespace Ledjon.ReplicatedMemoryCache.Tests;

public class VersionSourceTests
{
    [Fact]
    public void VersionSource_IsStrictlyMonotonic_EvenWhen_PhysicalTimeGoesBackwards()
    {
        Gen.Int[-100, 100].Array[1, 1000] // Deltas can be negative (clock drifted backward) or positive.
            .Sample(timeDeltas =>
            {
                var source = new VersionSource(nodeId: 1);
                long currentPhysicalTicks = 1_000_000;

                VersionTag lastVersion = default;

                foreach (var delta in timeDeltas)
                {
                    currentPhysicalTicks += delta;

                    var newVersion = source.GetNextVersion(currentPhysicalTicks);

                    if (lastVersion.Ticks != 0)
                    {
                        Assert.True(newVersion.Ticks > lastVersion.Ticks);
                        Assert.True(newVersion > lastVersion);
                    }

                    lastVersion = newVersion;
                }
            });
    }

    [Fact]
    public void VersionSource_CatchUp_ForcesCausality_IntoTheFuture()
    {
        var timeGenerator = Gen.Select(
            Gen.Long[100, 10_000],     // Local
            Gen.Long[100_000, 200_000] // Remote (forward in the future)
        );

        timeGenerator.Sample((localTicks, remoteTicks) =>
        {
            var source = new VersionSource(nodeId: 1);

            source.CatchUp(remoteTicks);

            var newVersion = source.GetNextVersion(localTicks);

            Assert.True(newVersion.Ticks > remoteTicks);
            Assert.Equal(remoteTicks + 1, newVersion.Ticks);
        });
    }

    [Fact]
    public void VersionSource_GeneratesUniqueTicks_UnderHighContention()
    {
        var source = new VersionSource(nodeId: 1);

        long frozenPhysicalTime = 1000;
        int threadCount = 10_000;
        var versions = new VersionTag[threadCount];

        Parallel.For(0, threadCount, i =>
        {
            versions[i] = source.GetNextVersion(frozenPhysicalTime);
        });

        var distinctTicks = versions.Select(x => x.Ticks).Distinct().Count();
        Assert.Equal(threadCount, distinctTicks);

        // Because time was frozen at 1000, the Interlocked logic must have 
        // artificially pushed the version source forward 10k times to guarantee uniqueness.

        var maxTick = versions.Max(x => x.Ticks);
        Assert.True(maxTick >= frozenPhysicalTime + threadCount - 1);
    }

    [Fact]
    public void VersionTag_TieBreakerDeterministicallyResolves_IdenticalTicks()
    {
        var nodeAVersion = new VersionTag(Ticks: 5000, NodeId: 100);
        var nodeBVersion = new VersionTag(Ticks: 5000, NodeId: 200);

        Assert.True(nodeBVersion > nodeAVersion);
        Assert.True(nodeAVersion < nodeBVersion);
        Assert.True(nodeAVersion == new VersionTag(5000, 100));
    }
}
