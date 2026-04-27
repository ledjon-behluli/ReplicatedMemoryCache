using Ledjon.ReplicatedMemoryCache;
using Ledjon.ReplicatedMemoryCache.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;
using ScottPlot;
using ScottPlot.TickGenerators;
using Ledjon.ReplicatedMemoryCache.StressTests;

var configs = new[]
{
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.10, ReconciliationMs: 1000),
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.30, ReconciliationMs: 1000),
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.50, ReconciliationMs: 1000),

    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.10, ReconciliationMs: 500),
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.30, ReconciliationMs: 500),
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.50, ReconciliationMs: 500),

    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.10, ReconciliationMs: 100),
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.30, ReconciliationMs: 100),
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.50, ReconciliationMs: 100),

    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.10, ReconciliationMs: 50),
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.30, ReconciliationMs: 50),
    new TestConfig(Workers: 25, TargetOpsPerSecond: 10000, WriteRatio: 0.50, ReconciliationMs: 50),
};

var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
var logger = loggerFactory.CreateLogger<Program>();

int configCount = 1;
int totalConfigs = configs.Length;
var runDuration = TimeSpan.FromSeconds(10);
var allResults = new List<TestRunResult>();

const int RunsPerConfig = 3;

foreach (var config in configs)
{
    StressTestCacheOptionsSource.Options.ReconciliationPeriod = TimeSpan.FromMilliseconds(config.ReconciliationMs);

    var currentConfigRuns = new List<TestRunResult>();

    for (int iteration = 1; iteration <= RunsPerConfig; iteration++)
    {
        logger.LogInformation("========================================================================");

        logger.LogInformation("=== Config {Current}/{Total} | Iteration {Iter}/{Total} | {Workers}w | {Ops} ops/s | {Write:P0}w | Recon {Recon}ms ===",
            configCount, totalConfigs, iteration, RunsPerConfig, config.Workers, config.TargetOpsPerSecond, config.WriteRatio, config.ReconciliationMs);

        var (cluster, clients, nodes) = await SetupClusterAsync();

        // Isolate the HotKeySet per iteration to prevent memory accumulation
        var keySet = new HotKeySet(configCount * 10 + iteration);
        var monitor = new ConvergenceMonitor(nodes, keySet, loggerFactory.CreateLogger<ConvergenceMonitor>());
        var runner = new CacheLoadRunner(nodes, keySet, config.TargetOpsPerSecond, config.WriteRatio);

        monitor.Start();

        var loadStart = DateTime.UtcNow;

        await runner.RunForAsync(runDuration);

        var loadEnd = DateTime.UtcNow;

        logger.LogInformation("Load stopped! Waiting for full convergence...");

        var convergenceStart = DateTime.UtcNow;

        while (DateTime.UtcNow - convergenceStart < TimeSpan.FromSeconds(45))
        {
            var lastRecord = monitor.History.LastOrDefault();
            if (lastRecord != default && lastRecord.Timestamp > loadEnd && lastRecord.ConvergenceRate >= 1.0)
            {
                break;
            }

            await Task.Delay(100);
        }

        monitor.Stop();
        await Task.Delay(1000); // Give monitor time to push its final record

        var history = monitor.History.ToList();
        var timeToFull = CalculateTimeToConvergence(history, loadEnd, 1.0);
        var loadPhaseHistory = history.Where(h => h.Timestamp <= loadEnd).ToList();
        var loadPhaseRates = loadPhaseHistory.Select(h => h.ConvergenceRate).ToList();
        var avgWritesPerSec = loadPhaseHistory.Count == 0 ? 0 : loadPhaseHistory.Average(h => h.WritesPerSecond);

        var metrics = new ConvergenceMetrics(
            P01: Percentile(loadPhaseRates, 0.01),
            P05: Percentile(loadPhaseRates, 0.05),
            P50: Percentile(loadPhaseRates, 0.50),
            P95: Percentile(loadPhaseRates, 0.95),
            P99: Percentile(loadPhaseRates, 0.99)
        );

        var result = new TestRunResult(config, history, metrics, timeToFull, loadEnd, avgWritesPerSec);
        currentConfigRuns.Add(result);

        logger.LogInformation("Tearing down cluster...");
        await TeardownAsync(cluster, clients);
    }

    var averagedResult = CalculateAverageResult(config, currentConfigRuns, runDuration);

    allResults.Add(averagedResult);

    configCount++;
}

GenerateAllPlots(allResults, logger);

logger.LogInformation("Stress test completed! Press any key to exit.");

Console.ReadKey();

async Task<(TestCluster cluster, List<IHost> clients, List<CacheConsumerNode> nodes)> SetupClusterAsync()
{
    var builder = new TestClusterBuilder(initialSilosCount: 3);
    builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;

    var cluster = builder.AddSiloBuilderConfigurator<TestSiloConfigurator>().Build();
    await cluster.DeployAsync();

    var client1 = await TestHelpers.CreateClientAsync<DefaultSerializerMarker>(cluster, StressTestCacheOptionsSource.Options, TimeProvider.System, [StressTestCacheOptionsSource.ScopeName]);
    var client2 = await TestHelpers.CreateClientAsync<DefaultSerializerMarker>(cluster, StressTestCacheOptionsSource.Options, TimeProvider.System, [StressTestCacheOptionsSource.ScopeName]);

    var nodes = TestHelpers.GetRandomNodes(cluster, [client1, client2], 5, StressTestCacheOptionsSource.ScopeName);

    return (cluster, [client1, client2], nodes);
}

async Task TeardownAsync(TestCluster cluster, List<IHost> clients)
{
    await Task.WhenAll(clients.Select(c => c.StopAsync()));
    await cluster.StopAllSilosAsync();
    await Task.Delay(2000);
}

static double Percentile(IEnumerable<double> sequence, double percentile)
{
    var sorted = sequence.OrderBy(x => x).ToArray();
    if (sorted.Length == 0) return 0;
    int index = (int)((sorted.Length - 1) * percentile);
    return sorted[Math.Min(index, sorted.Length - 1)];
}

static TimeSpan CalculateTimeToConvergence(List<ConvergenceRecord> history, DateTime loadEnd, double threshold)
{
    var recoveryPoint = history.FirstOrDefault(h => h.Timestamp > loadEnd && h.ConvergenceRate >= threshold);
    return recoveryPoint.Timestamp > loadEnd ? recoveryPoint.Timestamp - loadEnd : TimeSpan.FromSeconds(-1);
}

static TestRunResult CalculateAverageResult(TestConfig config, List<TestRunResult> runs, TimeSpan runDuration)
{
    var avgTimeToFull = TimeSpan.FromSeconds(runs.Average(r => r.TimeToFullConvergence.TotalSeconds));
    var avgWritesPerSec = runs.Average(r => r.AverageWritesPerSecond);

    var avgMetrics = new ConvergenceMetrics(
        P01: runs.Average(r => r.Metrics.P01),
        P05: runs.Average(r => r.Metrics.P05),
        P50: runs.Average(r => r.Metrics.P50),
        P95: runs.Average(r => r.Metrics.P95),
        P99: runs.Average(r => r.Metrics.P99)
    );

    double maxTimeLine = runs.Max(r => (r.History.Last().Timestamp - r.History.First().Timestamp).TotalSeconds);
    var baseTime = DateTime.UtcNow;
    var syntheticHistory = new List<ConvergenceRecord>();

    for (double time = 0; time <= maxTimeLine; time += 0.100) // Sample every 100ms
    {
        double sumRate = 0;

        foreach (var run in runs)
        {
            var firstTs = run.History.First().Timestamp;
            var currentRunDuration = (run.History.Last().Timestamp - firstTs).TotalSeconds;

            if (time > currentRunDuration)
            {
                sumRate += 1.0; // This run finished healing early, meaning its convergence is locked at 100%
            }
            else
            {
                var closest = run.History.MinBy(h => Math.Abs((h.Timestamp - firstTs).TotalSeconds - time));
                sumRate += closest.ConvergenceRate;
            }
        }

        syntheticHistory.Add(new ConvergenceRecord(
            Timestamp: baseTime.AddSeconds(time),
            ConvergenceRate: sumRate / runs.Count,
            Converged: 0, Sampled: 0, Tracked: 0, TotalWrites: 0, WritesPerSecond: avgWritesPerSec
        ));
    }

    return new TestRunResult(config, syntheticHistory, avgMetrics, avgTimeToFull, baseTime.Add(runDuration), avgWritesPerSec);
}

void GenerateAllPlots(List<TestRunResult> results, ILogger logger)
{
    var plotsPath = Path.Combine(AppContext.BaseDirectory, "plots");
    Directory.CreateDirectory(plotsPath);

    GenerateMinConvergenceGuaranteesChart(results, plotsPath);
    GenerateConvergenceTimelineChart(results, plotsPath);

    logger.LogInformation("Plots saved to: {Path}", plotsPath);
}

void GenerateMinConvergenceGuaranteesChart(List<TestRunResult> results, string path)
{
    // We are more interested in the P01/05 since they represent the worst-case convergence guarantees under sustained write contention.
    // But its a good idea to also show the P50 (median) for context.

    var plot = new Plot();

    plot.Title("Convergence Guarantees Under Sustained Write Contention (P50, P05, P01)");
    plot.YLabel("Convergence %");

    plot.Axes.SetLimitsY(0, 130);

    var bars = new List<Bar>();

    double currentX = 0;
    var tickPositions = new List<double>();
    var tickLabels = new List<string>();

    // Group by Write Ratio first
    var groupedByRatio = results.GroupBy(r => r.Config.WriteRatio).OrderBy(g => g.Key);

    foreach (var ratioGroup in groupedByRatio)
    {
        double ratioStartX = currentX;

        double groupAvgWps = ratioGroup.Average(r => r.AverageWritesPerSecond);
        var orderedRecons = ratioGroup.OrderBy(r => r.Config.ReconciliationMs).ToList();

        foreach (var r in orderedRecons)
        {
            double p50 = Math.Max(1, r.Metrics.P50 * 100);
            double p05 = Math.Max(1, r.Metrics.P05 * 100);
            double p01 = Math.Max(1, r.Metrics.P01 * 100);

            Color baseColor = GetReconColor(r.Config.ReconciliationMs);

            // P50
            bars.Add(new Bar
            {
                Position = currentX,
                Value = p50,
                FillColor = baseColor,
                Size = 0.85,
                LineWidth = 0,
                Label = $"P50\n{p50:0}%"
            });
            currentX++;

            // P05
            bars.Add(new Bar
            {
                Position = currentX,
                Value = p05,
                FillColor = baseColor,
                Size = 0.85,
                LineWidth = 0,
                Label = $"P05\n{p05:0}%"
            });
            currentX++;

            // P01
            bars.Add(new Bar
            {
                Position = currentX,
                Value = p01,
                FillColor = baseColor,
                Size = 0.85,
                LineWidth = 0,
                Label = $"P01\n{p01:0}%"
            });
            currentX++;

            currentX += 1.0;
        }

        double ratioEndX = currentX - 1.0;
        tickPositions.Add((ratioStartX + ratioEndX - 1) / 2.0);
        tickLabels.Add($"{ratioGroup.Key:P0} Writes\n(~{groupAvgWps:0} W/s)");

        currentX += 4.0;
    }

    plot.Add.Bars(bars);

    // Legend
    foreach (var reconMs in results.Select(x => x.Config.ReconciliationMs).Distinct().OrderBy(x => x))
    {
        var dummy = plot.Add.Scatter(new double[] { -100 }, new double[] { -100 });
        dummy.Color = GetReconColor(reconMs);
        dummy.LineWidth = 8;
        dummy.MarkerSize = 0;
        dummy.LegendText = $"{reconMs}ms Recon Period";
    }

    var legend = plot.ShowLegend(Alignment.UpperRight);
    legend.BackgroundColor = Colors.White.WithAlpha(0.9);

    // Lock X limits to effectively hide the dummy legend points
    plot.Axes.SetLimitsX(-2, currentX);

    Tick[] ticks = [.. tickPositions.Select((pos, i) => new Tick(pos, tickLabels[i]))];

    plot.Axes.Bottom.TickGenerator = new NumericManual(ticks);
    plot.Axes.Bottom.TickLabelStyle.FontSize = 14;

    // Render slightly wider so the side-by-side bars don't squish together
    plot.SavePng(Path.Combine(path, "MinConvergenceGuarantees.png"), 1600, 800);
}

void GenerateConvergenceTimelineChart(List<TestRunResult> results, string path)
{
    var plot = new Plot();

    plot.Title("Convergence Timeline During Test & Cooldown");
    plot.XLabel("Elapsed Time (seconds)");
    plot.YLabel("Convergence %");

    double stressDuration = runDuration.TotalSeconds;
    double maxTimeline = results.Max(r => (r.History.Last().Timestamp - r.History.First().Timestamp).TotalSeconds);

    plot.Axes.SetLimitsY(0, 105);
    plot.Axes.SetLimitsX(0, maxTimeline);

    var stressSpan = plot.Add.HorizontalSpan(0, stressDuration);
    stressSpan.FillColor = Colors.Red.WithAlpha(0.1);

    var cooldownSpan = plot.Add.HorizontalSpan(stressDuration, maxTimeline);
    cooldownSpan.FillColor = Colors.Green.WithAlpha(0.1);

    var t1 = plot.Add.Text("Stress Phase", stressDuration / 2, 50);
    t1.LabelFontColor = Colors.Red.WithAlpha(0.6);
    t1.LabelFontSize = 18;
    t1.LabelBold = true;
    t1.Alignment = Alignment.MiddleCenter;

    var t2 = plot.Add.Text("Cooldown Phase", stressDuration + ((maxTimeline - stressDuration) / 2), 50);
    t2.LabelFontColor = Colors.Green.WithAlpha(0.6);
    t2.LabelFontSize = 18;
    t2.LabelBold = true;
    t2.Alignment = Alignment.MiddleCenter;

    var boundaryLine = plot.Add.VerticalLine(stressDuration);
    boundaryLine.Color = Colors.Red;
    boundaryLine.LinePattern = LinePattern.Dashed;
    boundaryLine.LineWidth = 2;

    foreach (var result in results)
    {
        var firstTimestamp = result.History.First().Timestamp;

        var times = result.History.Select(h => (h.Timestamp - firstTimestamp).TotalSeconds).ToArray();
        var rates = result.History.Select(h => h.ConvergenceRate * 100).ToArray();
        var scatter = plot.Add.Scatter(times, rates);

        scatter.Color = GetTimelineColor(result.Config.WriteRatio, result.Config.ReconciliationMs);
        scatter.LinePattern = GetTimelinePattern(result.Config.ReconciliationMs);
        scatter.LineWidth = 2.5f;
        scatter.MarkerSize = 0;

        scatter.LegendText = $"{result.Config.WriteRatio:P0} W/R Ratio | ({result.AverageWritesPerSecond:0} avg [w/s]) | ReconPeriod: {result.Config.ReconciliationMs}[ms]";
    }

    var legend = plot.ShowLegend(Alignment.LowerRight);

    legend.BackgroundColor = Colors.White.WithAlpha(0.8);

    plot.SavePng(Path.Combine(path, "ConvergenceTimeline.png"), 1600, 900);
}

Color GetReconColor(int reconMs) => reconMs switch
{
    <= 50 => Colors.Blue,
    <= 100 => Colors.Green,
    <= 500 => Colors.Orange,
    _ => Colors.Red
};

Color GetTimelineColor(double writeRatio, int reconMs)
{
    // 10% Write Ratio -> Blue shades (Darker = Faster Recon)
    if (writeRatio <= 0.15)
    {
        return reconMs switch
        {
            <= 50 => Color.FromHex("#08306B"), // Very Dark Blue
            <= 100 => Color.FromHex("#2171B5"), // Medium Blue
            <= 500 => Color.FromHex("#4292C6"), // Light Blue
            _ => Color.FromHex("#9ECAE1")  // Pale Blue
        };
    }
    // 30% Write Ratio -> Green shades
    else if (writeRatio <= 0.35)
    {
        return reconMs switch
        {
            <= 50 => Color.FromHex("#00441B"), // Very Dark Green
            <= 100 => Color.FromHex("#238B45"), // Medium Green
            <= 500 => Color.FromHex("#41AB5D"), // Light Green
            _ => Color.FromHex("#A1D99B")  // Pale Green
        };
    }
    // 50% Write Ratio -> Red shades
    else
    {
        return reconMs switch
        {
            <= 50 => Color.FromHex("#67000D"), // Very Dark Red
            <= 100 => Color.FromHex("#CB181D"), // Medium Red
            <= 500 => Color.FromHex("#EF3B2C"), // Light Red
            _ => Color.FromHex("#FC9272")  // Pale Red
        };
    }
}

LinePattern GetTimelinePattern(int reconMs) => reconMs switch
{
    <= 50 => LinePattern.Solid,
    <= 100 => LinePattern.Dashed,
    <= 500 => LinePattern.DenselyDashed,
    _ => LinePattern.Dotted
};

public record struct ConvergenceRecord(
  DateTime Timestamp, double ConvergenceRate, int Converged,
  int Sampled, int Tracked, long TotalWrites, double WritesPerSecond);

public record struct ConvergenceMetrics(double P01, double P05, double P50, double P95, double P99);

public record struct TestConfig(int Workers, double TargetOpsPerSecond, double WriteRatio, int ReconciliationMs);

public record struct TestRunResult(
  TestConfig Config, List<ConvergenceRecord> History, ConvergenceMetrics Metrics,
  TimeSpan TimeToFullConvergence, DateTime LoadEndTime, double AverageWritesPerSecond);

public class TestSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder builder)
    {
        builder.Services.AddSingleton(TimeProvider.System);

        TestHelpers.ConfigureCacheReplication<DefaultSerializerMarker>(builder.Services, StressTestCacheOptionsSource.Options, [StressTestCacheOptionsSource.ScopeName]);
    }
}

public static class StressTestCacheOptionsSource
{
    public const string ScopeName = "default";
    public static readonly ReplicatedMemoryCacheOptions Options = new() { MaterializedSketchSize = 0 };
}