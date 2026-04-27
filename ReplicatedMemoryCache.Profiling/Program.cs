using ScottPlot;
using System.Diagnostics;
using Ledjon.ReplicatedMemoryCache;
using ScottPlot.TickGenerators;

Console.WriteLine("Starting RIBLT profiling...");

var histogramSnapshots = new Dictionary<int, long[]>();

foreach (var symbolsToProduce in new int[] { 1, 10, 100, 1_000, 10_000, 100_000, 1_000_000 })
{
    var encoder = new RibltEncoder();

    for (int i = 0; i < 1_000_000; i++)
    {
        encoder.AddMutation(new MutationSymbol(Guid.NewGuid()));
    }

    RibltProfiler.Reset();
    GC.Collect(); // So that we arent hit by a GC pause after having allocated a good a mount on the previous run!

    var watch = Stopwatch.StartNew();

    for (int i = 0; i < symbolsToProduce; i++)
    {
        encoder.ProduceNextSymbol();
    }

    watch.Stop();

    Console.WriteLine($"Finished producing {symbolsToProduce} coded symbols in {watch.ElapsedMilliseconds}ms.");

    PrintReport();
    GeneratePlot(symbolsToProduce);

    histogramSnapshots.Add(symbolsToProduce, (long[])RibltProfiler.Histogram.Clone());
}

GenerateCombinedPlot(histogramSnapshots);

Console.WriteLine("Press any key to exit..."); 
Console.ReadKey();

static void PrintReport()
{
    long symbols = RibltProfiler.SymbolsProduced == 0 ? 1 : RibltProfiler.SymbolsProduced;
    long totalIters = RibltProfiler.TotalLoopIterations == 0 ? 1 : RibltProfiler.TotalLoopIterations;
    double avgIters = (double)RibltProfiler.TotalLoopIterations / symbols;
    double updateRootRate = (double)RibltProfiler.UpdateRootCalls / totalIters * 100;

    Console.WriteLine();
    Console.WriteLine("==================================================");
    Console.WriteLine("                 RIBLT PROFILER REPORT            ");
    Console.WriteLine("==================================================");

    Console.WriteLine("--------------- HIGH-LEVEL METRICS ---------------");
    Console.WriteLine($"Symbols Produced:       {RibltProfiler.SymbolsProduced:N0}");
    Console.WriteLine($"Empty Symbols:          {RibltProfiler.EmptySymbols:N0}");
    Console.WriteLine($"Non-Empty Symbols:      {RibltProfiler.NonEmptySymbols:N0}");

    Console.WriteLine("\n--------------- WORKLOAD PERFORMANCE ---------------");
    Console.WriteLine($"Total Loop Iterations:  {RibltProfiler.TotalLoopIterations:N0}");
    Console.WriteLine($"Max Iterations/Symbol:  {RibltProfiler.MaxLoopIterations:N0}");
    Console.WriteLine($"Avg Iterations/Symbol:  {avgIters:N2}");

    Console.WriteLine("\n--------------- ALGORITHM BEHAVIOR ---------------");
    Console.WriteLine($"UpdateRoot Calls:      {RibltProfiler.UpdateRootCalls:N0}");
    Console.WriteLine($"UpdateRoot Rate:       {updateRootRate:F2}%");

    Console.WriteLine("\n--------------- ITERATION HISTOGRAM (Bucket by Log2) ---------------");

    for (int i = 0; i < RibltProfiler.Histogram.Length; i++)
    {
        if (RibltProfiler.Histogram[i] > 0)
        {
            long rangeStart = i == 0 ? 0 : 1L << i;
            long rangeEnd = i == 0 ? 1 : (1L << (i + 1)) - 1;

            Console.WriteLine($"Bucket {i,2} [{rangeStart,9:N0} -> {rangeEnd,9:N0}] : {RibltProfiler.Histogram[i]:N0} symbols");
        }
    }

    Console.WriteLine("=========================================================\n");
}

static void GeneratePlot(int symbolsToProduce)
{
    int maxBucket = 0; // highest bucket that actually has data so we don't plot trailing empty space.

    for (int i = 0; i < RibltProfiler.Histogram.Length; i++)
    {
        if (RibltProfiler.Histogram[i] > 0)
        {
            maxBucket = i;
        }
    }

    if (maxBucket == 0 && RibltProfiler.Histogram[0] == 0)
    {
        return;
    }

    var ticks = new List<Tick>();
    var values = new double[maxBucket + 1];
    var positions = new double[maxBucket + 1];

    for (int i = 0; i <= maxBucket; i++)
    {
        values[i] = RibltProfiler.Histogram[i];
        positions[i] = i;

        long rangeStart = i == 0 ? 0 : 1L << i;
        long rangeEnd = i == 0 ? 1 : (1L << (i + 1)) - 1;

        string startStr = rangeStart switch
        {
            >= 1_000_000 => (rangeStart / 1_000_000) + "m",
            >= 1_000 => (rangeStart / 1_000) + "k",
            _ => rangeStart.ToString()
        };

        string endStr = rangeEnd switch
        {
            >= 1_000_000 => (rangeEnd / 1_000_000) + "m",
            >= 1_000 => (rangeEnd / 1_000) + "k",
            _ => rangeStart.ToString()
        };

        string label = $"B{i}\n[{startStr}-{endStr}]";

        ticks.Add(new Tick(i, label));
    }

    var plot = new Plot();
    var barPlot = plot.Add.Bars(positions, values);

    foreach (var bar in barPlot.Bars)
    {
        bar.FillColor = Colors.CornflowerBlue;
    }

    plot.Title("R-IBLT Encoder Symbol Production Histogram");
    plot.YLabel("Number of Symbols Produced (Count)");
    plot.XLabel("Iteration Count Buckets (Log2)");

    plot.Axes.Bottom.TickGenerator = new NumericManual([.. ticks]);
    plot.Axes.Bottom.TickLabelStyle.Rotation = -45; 
    plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleRight;

    plot.SavePng($"riblt_histogram_{symbolsToProduce}.png", 1400, 700);
}

static void GenerateCombinedPlot(Dictionary<int, long[]> snapshots)
{
    int maxBucket = 0;

    foreach (var hist in snapshots.Values)
    {
        for (int i = 0; i < hist.Length; i++)
        {
            if (hist[i] > 0 && i > maxBucket)
            {
                maxBucket = i;
            }
        }
    }

    if (maxBucket == 0)
    {
        return;
    }

    var plot = new Plot();
    var ticks = new List<Tick>();

    for (int i = 0; i <= maxBucket; i++)
    {
        long rangeStart = i == 0 ? 0 : 1L << i;
        long rangeEnd = i == 0 ? 1 : (1L << (i + 1)) - 1;

        string startStr = rangeStart switch
        {
            >= 1_000_000 => (rangeStart / 1_000_000) + "m",
            >= 1_000 => (rangeStart / 1_000) + "k",
            _ => rangeStart.ToString()
        };

        string endStr = rangeEnd switch
        {
            >= 1_000_000 => (rangeEnd / 1_000_000) + "m",
            >= 1_000 => (rangeEnd / 1_000) + "k",
            _ => rangeStart.ToString()
        };

        ticks.Add(new Tick(i, $"B{i}\n[{startStr}-{endStr}]"));
    }

    foreach (var kvp in snapshots)
    {
        int symbols = kvp.Key;
        long[] hist = kvp.Value;

        double[] xs = new double[maxBucket + 1];
        double[] ys = new double[maxBucket + 1];

        for (int i = 0; i <= maxBucket; i++)
        {
            xs[i] = i;
            ys[i] = hist[i];
        }

        var scatter = plot.Add.ScatterLine(xs, ys);

        scatter.LineWidth = 3;
        scatter.LegendText = $"{symbols:N0} symbols";
    }

    plot.Title("Combined R-IBLT Encoder Symbol Production Histogram");
    plot.YLabel("Number of Symbols Produced (Count)");
    plot.XLabel("Iteration Count Buckets (Log2)");

    plot.Axes.Bottom.TickGenerator = new NumericManual([.. ticks]);
    plot.Axes.Bottom.TickLabelStyle.Rotation = -45;
    plot.Axes.Bottom.TickLabelStyle.Alignment = Alignment.MiddleRight;

    plot.ShowLegend(Alignment.UpperRight);

    plot.SavePng("riblt_histogram_combined.png", 1400, 700);
}