using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;
using Ledjon.ReplicatedMemoryCache;
using System.Runtime.CompilerServices;

var exit = false;

while (!exit)
{
    Console.Clear();
    Console.WriteLine("Select a benchmark to run:");
    Console.WriteLine("1. FirstSymbolBenchmarks");
    Console.WriteLine("2. ProduceNextSymbolBenchmarks");
    Console.WriteLine("3. SketchBenchmarks");
    Console.WriteLine("4. GeneratorFunctionBenchmarks");
    Console.WriteLine("5. Exit");
    Console.Write("\nEnter: ");

    string? choice = Console.ReadLine();

    Console.Clear();

    switch (choice)
    {
        case "1":
            Console.WriteLine("Running FirstSymbolBenchmarks...\n");
            BenchmarkRunner.Run<FirstSymbolBenchmarks>();
            break;

        case "2":
            Console.WriteLine("Running ProduceNextSymbolBenchmarks...\n");
            BenchmarkRunner.Run<ProduceNextSymbolBenchmarks>();
            break;

        case "3":
            Console.WriteLine("Running SketchBenchmarks...\n");
            BenchmarkRunner.Run<SketchBenchmarks>();
            break;

        case "4":
            Console.WriteLine("Running GeneratorFunctionBenchmarks...\n");
            BenchmarkRunner.Run<GeneratorFunctionBenchmarks>();
            break;

        case "5":
            exit = true;
            Console.WriteLine("Exiting...");
            continue;

        default:
            Console.WriteLine("Invalid selection. Please choose a number between 1 and 4.");
            break;
    }

    if (!exit)
    {
        Console.WriteLine("\nBenchmark completed. Press any key to return to the menu...");
        Console.ReadKey();
    }
}

[MemoryDiagnoser]
public class FirstSymbolBenchmarks
{
    private RibltEncoder _encoder = null!;

    [Params(1_000, 10_000, 100_000, 1_000_000)]
    public int CacheSize { get; set; }

    [IterationSetup]
    public void Setup()
    {
        _encoder = new RibltEncoder();

        for (int i = 0; i < CacheSize; i++)
        {
            _encoder.AddMutation(new MutationSymbol(Guid.NewGuid()));
        }
    }

    [Benchmark]
    public long FirstSymbol()
    {
        return _encoder.ProduceNextSymbol().Count;
    }
}

[MemoryDiagnoser]
[CategoriesColumn]
public class ProduceNextSymbolBenchmarks
{
    private RibltEncoder _encoder = null!;

    [Params(1_000, 10_000, 100_000, 1_000_000)]
    public int CacheSize { get; set; }

    /// <summary>
    /// In R-IBLT, the number of symbols required to successfully decode and reconcile two sets 
    /// is directly proportional to the number of differences (<c>d</c>) between them, not the total size of the sets.
    /// The paper proves mathematical that as <c>d</c> grows, the algorithm reconciles <c>d</c> differences using 
    /// approximately 1.35<c>d</c> symbols (ranging up to 1.72<c>d</c> for very small values of <c>d</c>).
    /// The values tested here simulate the computational cost of fixing set differences of various size, they are
    /// not representative of how many symbols it actually takes.
    /// </summary>
    [Params(1_000, 10_000, 100_000, 1_000_000)]
    public int SymbolsToProduce { get; set; }

    [IterationSetup]
    public void Setup()
    {
        _encoder = new RibltEncoder();

        for (int i = 0; i < CacheSize; i++)
        {
            _encoder.AddMutation(new MutationSymbol(Guid.NewGuid()));
        }
    }

    [Benchmark]
    public long ProduceNextSymbol()
    {
        long total = 0;

        for (int i = 0; i < SymbolsToProduce; i++)
        {
            total += _encoder.ProduceNextSymbol().Count;
        }

        return total;
    }
}

[MemoryDiagnoser]
[CategoriesColumn]
public class SketchBenchmarks
{
    private RibltSketch _sketch = null!;
    private CodedSymbol[] _sliceBuffer = null!;
    private List<MutationSymbol> _mutations = null!;

    [Params(1_000, 10_000, 100_000, 1_000_000)]
    public int CacheSize { get; set; }

    [Params(8192)]
    public int SketchSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sketch = new(SketchSize);
        _sliceBuffer = new CodedSymbol[SketchSize];
        _mutations = new List<MutationSymbol>(CacheSize);

        for (int i = 0; i < CacheSize; i++)
        {
            var mutation = new MutationSymbol(Guid.NewGuid());

            _mutations.Add(mutation);

            // We pre-warm the sketch just like the cache does over time.
            _sketch.ApplyMutation(mutation, SymbolOperation.Add);
        }
    }

    [Benchmark(Baseline = true)]
    public long Encoder_ProducePrefix()
    {
        // We need a fresh encoder for every run since its stateful.
        // This is more realistic since an encoder would be created per-reconcilliation round.
        // Altough we could be keeping one as it has the linearity property, but thats a whole can of worms! 

        var encoder = new RibltEncoder();

        foreach (var mutation in _mutations)
        {
            encoder.AddMutation(mutation);
        }

        long total = 0;

        for (int i = 0; i < SketchSize; i++)
        {
            total += encoder.ProduceNextSymbol().Count;
        }

        return total;
    }

    [Benchmark]
    public long Sketch_ReadPrefix()
    {
        var destSpan = _sliceBuffer.AsSpan(0, SketchSize);
        var readCount = _sketch.ReadSlice(0, destSpan);

        long total = 0;

        for (int i = 0; i < readCount; i++)
        {
            total += destSpan[i].Count;
        }

        return total;
    }
}

[MemoryDiagnoser]
public class GeneratorFunctionBenchmarks
{
    private const int GenerationsLimit = 10_000;
    private const ulong Seed = 0x123456789ABCDEF0;

    [Benchmark(Baseline = true)]
    public long NextIndex()
    {
        long total = 0;
        var mapping = new RandomMapping(Seed);

        for (int i = 0; i < GenerationsLimit; i++)
        {
            total += mapping.NextIndex();
        }

        return total;
    }

    [Benchmark]
    public long NextIndex_Naive()
    {
        long total = 0;
        var mapping = new NaiveRandomMapping(Seed);

        for (int i = 0; i < GenerationsLimit; i++)
        {
            total += mapping.NextIndex();
        }

        return total;
    }

    /// <summary>
    /// Computes the inverse CDF by literally calculating 'r' as 
    /// opposed to the substitution trick which eliminates all fp power operations.
    /// </summary>
    private struct NaiveRandomMapping(ulong hash, long startIndex = 0)
    {
        public ulong Prng = hash;
        public long LastIndex = startIndex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long NextIndex()
        {
            Prng *= 0xda942042e4dd58b5UL;

            double r = 1.0 - (Prng / 18446744073709551616.0); // 1 - (Prng / 2^64)

            double inverseCdfSample = Math.Pow(1.0 - r, -0.5) - 1.0;
            double jump = Math.Ceiling((LastIndex + 1.5) * inverseCdfSample);
            long nextIndex = Math.Max(1L, jump >= int.MaxValue ? int.MaxValue : (long)jump);

            LastIndex += nextIndex;

            return LastIndex;
        }
    }
}