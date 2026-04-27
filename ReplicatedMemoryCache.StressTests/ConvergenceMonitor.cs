using Ledjon.ReplicatedMemoryCache.Tests;
using Microsoft.Extensions.Logging;
using ScottPlot;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ledjon.ReplicatedMemoryCache.StressTests;

public class ConvergenceMonitor(List<CacheConsumerNode> nodes, HotKeySet keySet, ILogger<ConvergenceMonitor> logger)
{
    private Thread? _thread;

    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<ConvergenceMonitor> _logger = logger;
    private readonly ConcurrentQueue<ConvergenceRecord> _history = new();

    public IReadOnlyList<ConvergenceRecord> History => [.. _history];

    public void Start()
    {
        _thread = new Thread(() =>
        {
            var lastWrites = 0L;
            var lastTime = DateTime.UtcNow;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount - 1,
                CancellationToken = _cts.Token
            };

            try
            {
                Debug.Assert(!Thread.CurrentThread.IsThreadPoolThread);

                while (!_cts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
                {
                    int converged = 0, sampled = 0;
                    var keys = keySet.GetSample(2500);

                    Parallel.ForEach(keys, options, key =>
                    {
                        try
                        {
                            var values = nodes.Select(n => n.Get(key)).ToList();

                            Interlocked.Increment(ref sampled);

                            if (values.Distinct(StringComparer.Ordinal).Count() == 1)
                            {
                                Interlocked.Increment(ref converged);
                            }
                        }
                        catch
                        {
                            // Ignore
                        }
                    });

                    var currentWrites = keySet.TotalWrites;
                    var now = DateTime.UtcNow;

                    var elapsedSecs = (now - lastTime).TotalSeconds;
                    var writesPerSec = elapsedSecs > 0 ? (currentWrites - lastWrites) / elapsedSecs : 0;

                    lastWrites = currentWrites;
                    lastTime = now;

                    var record = new ConvergenceRecord(
                        Timestamp: now,
                        ConvergenceRate: sampled > 0 ? (double)converged / sampled : 0,
                        Converged: converged,
                        Sampled: sampled,
                        Tracked: keySet.TrackedCount,
                        TotalWrites: currentWrites,
                        WritesPerSecond: writesPerSec
                    );

                    _history.Enqueue(record);

                    _logger.LogInformation("[{Time}] Conv: {Rate:P1} | Writes/s: {WPS:0} | Tracked: {Tracked}",
                        record.Timestamp.ToString("HH:mm:ss"), record.ConvergenceRate, record.WritesPerSecond, record.Tracked);
                }
            }
            catch
            {
                Debug.Assert(_cts.IsCancellationRequested);
            }
        })
        {
            IsBackground = true
        };

        _thread.Start();
    }

    public void Stop()
    {
        _cts.Cancel();

        if (_thread is not null && _thread.IsAlive)
        {
            _thread.Join();
        }

        _cts.Dispose();
    }
}
