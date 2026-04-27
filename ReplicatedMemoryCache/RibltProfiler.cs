using System.Numerics;
using System.Runtime.CompilerServices;

namespace Ledjon.ReplicatedMemoryCache;

internal static class RibltProfiler
{
    public static long TotalLoopIterations;
    public static long MaxLoopIterations;
    public static long UpdateRootCalls;
    public static long SymbolsProduced;
    public static long EmptySymbols;    // while loop never executed (none of the source symbols mapped to the coded symbol X (some number in the infitite sequence))
    public static long NonEmptySymbols; // while loop executed at least once (at least one source symbol mappend to the coded symbol Y (some number in the infitite sequence))

    /// <summary>
    /// Tells how many source symbols contribute to any given coded symbol.
    /// </summary>
    public static readonly long[] Histogram = new long[33]; // 2^33 covers 8,589,934,592 symbols to produce so its plenty. 

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordProducedSymbol(long loopIterations)
    {
        SymbolsProduced++;
        TotalLoopIterations += loopIterations;

        if (loopIterations > MaxLoopIterations)
        {
            MaxLoopIterations = loopIterations;
        }

        if (loopIterations == 0)
        {
            EmptySymbols++;
        }
        else
        {
            NonEmptySymbols++;
        }

        int bucket = Math.Min(32, BitOperations.Log2((uint)Math.Max(1, loopIterations)));
        Histogram[bucket]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordUpdateRoot()
    {
        UpdateRootCalls++;
    }

    public static void Reset()
    {
        TotalLoopIterations = 0;
        MaxLoopIterations = 0;
        UpdateRootCalls = 0;
        SymbolsProduced = 0;
        EmptySymbols = 0;
        NonEmptySymbols = 0;
        Array.Clear(Histogram);
    }
}