using Ledjon.ReplicatedMemoryCache.Tests;
using System.Diagnostics;

namespace Ledjon.ReplicatedMemoryCache.StressTests;

public class CacheLoadRunner(List<CacheConsumerNode> nodes, HotKeySet keySet, double targetOpsPerSecond = 8000, double writeRatio = 0.25)
{
    private readonly double _targetOpsPerSecond = Math.Max(100, targetOpsPerSecond);

    public async Task RunForAsync(TimeSpan duration)
    {
        long totalOps = 0;
        var overallWatch = Stopwatch.StartNew();
        var intervalWatch = Stopwatch.StartNew();

        const int TargetBatchSize = 200;

        while (overallWatch.Elapsed < duration)
        {
            for (int i = 0; i < TargetBatchSize; i++)
            {
                if (overallWatch.Elapsed >= duration) break;

                var node = nodes[Random.Shared.Next(nodes.Count)];
                var key = keySet.GetRandomKey();

                if (Random.Shared.NextDouble() < writeRatio)
                {
                    node.Set(key, $"v_{Guid.NewGuid():N}");
                    keySet.RecordWrite(key);
                }
                else
                {
                    node.Get(key);
                }

                totalOps++;
            }

            var elapsedMs = intervalWatch.ElapsedMilliseconds;
            var expectedMs = (totalOps * 1000.0) / _targetOpsPerSecond;

            if (elapsedMs < expectedMs)
            {
                if ((int)(expectedMs - elapsedMs) is { } sleepMs && sleepMs > 0)
                {
                    await Task.Delay(sleepMs);
                }
            }

            if (intervalWatch.ElapsedMilliseconds > 1500)
            {
                intervalWatch.Restart();
            }
        }
    }
}
