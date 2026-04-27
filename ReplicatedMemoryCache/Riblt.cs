using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Ledjon.ReplicatedMemoryCache;

[GenerateSerializer]
[Alias("Ledjon.ReplicatedMemoryCache.MutationSymbol")]
internal readonly record struct MutationSymbol(Guid Id)
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MutationSymbol Xor(MutationSymbol other) =>  
        new(Vector128.IsHardwareAccelerated ? SimdXor(Id, other.Id) : ScalarXor(Id, other.Id));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid SimdXor(Guid id, Guid otherId)
    {
        Guid result = default;

        var idVec = Vector128.LoadUnsafe(ref Unsafe.As<Guid, byte>(ref id));
        var otherIdVec = Vector128.LoadUnsafe(ref Unsafe.As<Guid, byte>(ref otherId));
        var resultVec = idVec ^ otherIdVec;

        resultVec.StoreUnsafe(ref Unsafe.As<Guid, byte>(ref result));

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid ScalarXor(Guid id, Guid otherId)
    {
        Guid result = default;

        // This means there is no SIMD 128 available, so we fallback to doing word-level parallelism since we know
        // the id will be fixed 16 bytes long. We first point to the very beginning
        // of the Guids as raw bytes and read them as unaligned 64-bit integers.
        // Instead of processing 16 individual bytes, we process the GUID as two 64-bit chunks,
        // this way we can reduce the work from 16 -> 2 XOR operations.

        const int WordLength = 8;

        ref byte idBytes = ref Unsafe.As<Guid, byte>(ref id);
        ref byte otherBytes = ref Unsafe.As<Guid, byte>(ref otherId);
        ref byte resultBytes = ref Unsafe.As<Guid, byte>(ref result);

        // We use unaligned reads here because Guid storage is not guaranteed to be
        // naturally aligned on every architecture. This allows the runtime to generate
        // the appropriate code regardless of the underlying CPU alignment requirements.

        // First we read and XOR the first 8 bytes of both Guids together.
        ulong low = Unsafe.ReadUnaligned<ulong>(ref idBytes) ^ Unsafe.ReadUnaligned<ulong>(ref otherBytes);

        // Next we advance exactly 8 bytes into the second half of each Guid and XOR the remaining 8 bytes together.
        ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref idBytes, WordLength)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref otherBytes, WordLength));

        // Finally we write both XORed halves back into the result Guid.
        // We use unaligned writes for the same reason as the reads above.

        Unsafe.WriteUnaligned(ref resultBytes, low);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref resultBytes, WordLength), high);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetHash()
    {
        Guid id = Id;

        const int WordLength = 8;

        // Same idea as the XOR method above, we treat the Guid as two 64-bit chunks and read them as unaligned integers.
        // The word-level parallelism allows us to reduce the number of operations from 16 -> 2, which is a nice perf win.

        ref byte idBytes = ref Unsafe.As<Guid, byte>(ref id);

        ulong low = Unsafe.ReadUnaligned<ulong>(ref idBytes);
        ulong high = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref idBytes, WordLength));

        // The golden ratio fraction is used to mix the two halves of the Guid together in a way that
        // produces a well-distributed hash value. The gist is that the golden ratio being the "most irrational" number,
        // which helps to ensure that the hash values are uniformly distributed across the 64-bit space.
        const ulong GoldenRatioFraction = 0x9e3779b97f4a7c15UL;

        ulong mixedHash = low ^ (high + GoldenRatioFraction);

        // We further mix the upper and lower 32-bit halves of the hash.
        // This propagates information from the upper half into the lower half,
        // reducing correlations and improving the distribution of the resulting hash.
        mixedHash ^= mixedHash >> 32;

        // PRNGs (like the one used in RandomMapping) break down if initialized or updated with a seed value of exactly 0,
        // becoming permanently stuck at 0. We guard against this by substituting 1 as a fallback seed state.
        return mixedHash == 0 ? 1 : mixedHash;
    }
}

internal enum SymbolOperation
{
    Add = 1,
    Remove = -1
}

internal readonly record struct HashedSymbol(MutationSymbol Symbol, ulong Hash);

internal struct SymbolMapping
{
    public int SourceIndex;
    public long CodedIndex;
}

/// <summary>
/// A "Coded Symbol" (or Cell) as defined in the Rateless IBLT paper.
/// </summary>
[GenerateSerializer]
[Alias("Ledjon.ReplicatedMemoryCache.CodedSymbol")]
internal struct CodedSymbol
{
    [Id(0)] public MutationSymbol SymbolSum;
    [Id(1)] public ulong CheckSum;
    [Id(2)] public long Count;

    /// <summary>
    /// Adds/Removes an individual mutation's hash to this coded symbol.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(HashedSymbol symbol, SymbolOperation operation)
    {
        // XOR is its own inverse, so adding/removing a sum uses the exact same bitwise operation.
        SymbolSum = SymbolSum.Xor(symbol.Symbol);
        CheckSum ^= symbol.Hash;
        // The count is directionally adjusted (+1 or -1) based on the operation.
        Count += (long)operation;
    }

    /// <summary>
    /// Adds/Removes the state of another coded symbol to this symbol, in-place.
    /// </summary>
    /// <remarks>
    /// Used for combining sets or computing the mathematical difference between two distinct symbols.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(CodedSymbol other, SymbolOperation operation)
    {
        // XOR handles both the union and difference of the sums. 
        // Any items shared between the two symbols will naturally cancel each other out to 0.
        SymbolSum = SymbolSum.Xor(other.SymbolSum);
        CheckSum ^= other.CheckSum;
        // The other symbol's count is scaled by the operation (+1 or -1) before being applied.
        Count += other.Count * (long)operation;
    }

    public readonly bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Count == 0 && CheckSum == 0;
    }

    public readonly bool IsDecodable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // A cell is decodable/"pure" if count is 1 or -1, and hash(SymbolSum) = CheckSum,
        // akin to the regular IBLT where the hash(IdSum) = HashSum.
        get => (Count == -1 || Count == 1) && SymbolSum.GetHash() == CheckSum;
    }
}

/// <summary>
/// A source of generating an infinite, deterministic "schedule" of future coded indexes, where the source symbol will appear.
/// </summary>
/// <param name="hash">
/// The hash of the source symbol.
/// This acts as the seed so the PRNG is determinisitc.
/// </param>
/// <param name="startIndex">
/// The index of the "current" coded symbol. 
/// When its 0 it means start from the begning of the infinite coded symbol stream.
/// </param>
internal struct RandomMapping(ulong hash, long startIndex = 0)
{
    public ulong Prng = hash;
    public long LastIndex = startIndex;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long NextIndex()
    {
        // We generate the next index via the generator function (an inverse CMF) of the form:
        // C^(-1)(r) = (1.5 + i) * ((1 - r)^(-1/2) - 1); where r is a fp random variable r∈[0,1)
        // We define inverseCdfSample = ((1 - r)^(-1/2) - 1), which is the random factor drawn from the inverse CDF

        // This is the deterministic PRNG update step taken from the paper, which provides
        // high quality randomness, assuming the multiplier is coprime to 2^64.
        Prng *= 0xda942042e4dd58b5UL; 
                                      
        // Instead of using Random to generate 'r', we use a trick, where: 2^32 / sqrt(X), where X~U[0,2^64)
        // has approximately the same distribution as: (1 - r)^(-1/2)
        // So if we define X = Prng + 1, and r = 1 - X / 2^64
        // Then 1 - r = X / 2^64
        // We can substitute this into the genetator's ((1 - r)^(-1/2) - 1) = ((1 - 1 + X / 2^64)^(-1/2) - 1) = ((X / 2^64)^(-1/2) - 1) 
        // Where ((sqrt(2^64)/sqrt(X)) - 1); and (sqrt(2^64) = 2^32 yields to: ((2^32 / sqrt(X)) - 1) 
        // Where X = Prng + 1, we add 1 because the Prng can be 0, which would result in 2^32 / 0 -> ∞
        // Ofc 2^32 being excatly 4294967296

        double inverseCdfSample = (4294967296.0 / Math.Sqrt(Prng + 1.0)) - 1.0;

        // This will have a range than of [1, 2^64] (since we added +1):

        // For Prng = 2^64 - 1 -> sqrt(Prng + 1) = sqrt(2^64 - 1 + 1) = sqrt(2^64) = 2^32;
        // inverseCdfSample = (2^32 / 2^32) - 1 = 1 - 1 = 0, therefore giving in the smallest possible jump.

        // For Prng = 0 -> sqrt(Prng + 1) = sqrt(0 + 1) = 1
        // inverseCdfSample = (2^32 / 1) - 1 = 2^32 - 1 = 4294967295, therefore giving in the largest possible jump.

        // This results in a heavy-tailed distribution which is exactly what the random mapping construction requires according to the paper.
        // The probability that the next source symbol maps to the i-th coded symbol drops sharply as 'i' increases.

        // We need to ensure strict monotonic progress. The inverse-CDF can produce a zero jump when r is very close to 1.
        // Large jumps are fine, any index beyond the target filter/array size is naturally out-of-bounds, and .NET arrays
        // themselves are limited to int.MaxValue elements. Note that the 'jump' is the next index itself, i.e. the result.
        double jump = Math.Ceiling((LastIndex + 1.5) * inverseCdfSample);

        // We must cap the jump to prevent exponential long-overflow wrap-arounds, because inverseCdfSample can be up to 4.2 billion,
        // and LastIndex will exceed long.MaxValue in just 2 calls without a clapper. We also need to ensure strict monotonic progress
        // so we take Max(1, nextIndex) in case nextIndex = 0 (which it cant as startIndex = 0)

        long nextIndex = Math.Max(1L, jump >= int.MaxValue ? int.MaxValue : (long)jump);

        LastIndex += nextIndex;

        return LastIndex;
    }
}

/// <summary>
/// A high-perf, min-heap (priority queue) custom built for the R-IBLT encoder.
/// Elements are ordered by their target <see cref="SymbolMapping.CodedIndex"/>.
/// The root/top element always represents the symbol scheduled for the lowest/earliest future index.
/// This allows <see cref="CodingWindow"/> to efficiently evaluate the rateless stream 
/// chronologically (from index 0 upwards to infinity) in O(log N) time.
/// </summary>
internal class MappingHeap
{
    private readonly List<SymbolMapping> _mappings = [];

    public int Count => _mappings.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // SymbolMapping is a mutable struct so by returning by 'ref' we avoid a copy and allow the caller to modify the root directly.
    // Note that the caller must call UpdateRoot() after modifying the root, otherwise the heap property will be violated!
    // GetReference skips the bounds check, but the encoder must ensure they do not call this when the heap is empty (which it does not).
    public ref SymbolMapping Peek() => ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(_mappings));

    public void Add(SymbolMapping mapping)
    {
        _mappings.Add(mapping);

        var span = CollectionsMarshal.AsSpan(_mappings);
        int current = span.Length - 1;

        ref SymbolMapping ptr = ref MemoryMarshal.GetReference(span);
        long newCodedIndex = mapping.CodedIndex;

        while (current > 0)
        {
            int parentIndex = (current - 1) >> 1; // Essentially (current - 1) / 2, which is the parent index in a binary heap.
            ref SymbolMapping parent = ref Unsafe.Add(ref ptr, parentIndex);

            if (parent.CodedIndex <= newCodedIndex)
            {
                break; // The parent is already smaller than or equal to the new mapping, so we are done.
            }

            Unsafe.Add(ref ptr, current) = parent;

            current = parentIndex;
        }

        // Now we drop the new mapping into its final resting place.
        Unsafe.Add(ref ptr, current) = mapping;
    }

    /// <summary>
    /// Rebalances the heap after the root has been mutated in-place.
    /// </summary>
    /// <remarks>Call after having mutated the reference returned by <see cref="Peek"/></remarks>
    public void UpdateRoot()
    {
        var span = CollectionsMarshal.AsSpan(_mappings);
        int count = span.Length;

        if (count <= 1)
        {
            return; // Nothing to do, mutating the root does not violate the heap property if there is only one (or no) element.
        }

        ref SymbolMapping ptr = ref MemoryMarshal.GetReference(span);

        // We must save a copy of the mutated root before we start shifting things up,
        // otherwise we will overwrite it on the very first iteration!
        SymbolMapping rootMapping = ptr;

        int current = 0;
        int limit = (count - 1) / 2; // The last parent index that has at least one child. Any index beyond this is guaranteed to be a leaf node.
        long newCodedIndex = rootMapping.CodedIndex;

        // We sift down to the bottom of the heap, blindly pulling up the minimum child at each step.
        // Floyd's Optimization: https://en.wikipedia.org/wiki/Binary_heap#Building_a_heap

        // This is the first phase of the "sift down" operation, where we are creating a "hole"
        // at the root and pulling up the smaller child to fill it. Note that UpdateRoot() is called
        // many many times from the encoder, so we want to minimize the number of swaps and copies we do.
        // Profiling shows that the majority of the time is spent moving the root down the heap, because 
        // the R-IBLT is a heavy-tailed distribution, so most source symbols will map to the first few coded symbols,
        // and only a few will map to the later coded symbols. On every "jump", its almost guaranteed that the root
        // will be replaced with a child, and then that child will be replaced with its child, and so on.

        while (current < limit)
        {
            int leftChildIndex = (current * 2) + 1;
            int rightChildIndex = leftChildIndex + 1;

            ref SymbolMapping left = ref Unsafe.Add(ref ptr, leftChildIndex);
            ref SymbolMapping right = ref Unsafe.Add(ref ptr, rightChildIndex);

            int minChildIndex = left.CodedIndex <= right.CodedIndex ? leftChildIndex : rightChildIndex;

            Unsafe.Add(ref ptr, current) = Unsafe.Add(ref ptr, minChildIndex);

            current = minChildIndex;
        }

        // We have to check if the current node has a single child, because the last parent may only have one child.
        int half = count >> 1; // Essentially count / 2, which is the first index that is guaranteed to have at least one child.

        if (current < half)
        {
            int leftChildIndex = (current << 1) + 1;
            Unsafe.Add(ref ptr, current) = Unsafe.Add(ref ptr, leftChildIndex);
            current = leftChildIndex;
        }

        // We sift up to final resting place, which means we are done with the "hole"
        // and we can place the saved root element into its final position.
        while (current > 0)
        {
            int parentIndex = (current - 1) / 2;
            ref SymbolMapping parent = ref Unsafe.Add(ref ptr, parentIndex);

            if (parent.CodedIndex <= newCodedIndex)
            {
                break;
            }

            Unsafe.Add(ref ptr, current) = parent;
            current = parentIndex;
        }

        // Now we just drop the saved root into its final resting place.
        Unsafe.Add(ref ptr, current) = rootMapping;
    }

    public void Clear() => _mappings.Clear();
}

internal class CodingWindow
{
    private readonly MappingHeap _queue = new();
    private readonly List<HashedSymbol> _symbols = [];
    private readonly List<RandomMapping> _mappings = [];

    public long NextIndex { get; internal set; }
    public IReadOnlyList<HashedSymbol> Symbols => _symbols;

    public void AddHashedSymbol(HashedSymbol symbol) => AddHashedSymbol(symbol, new RandomMapping(symbol.Hash));
    
    public void AddHashedSymbol(HashedSymbol symbol, RandomMapping mapping)
    {
        _symbols.Add(symbol);
        _mappings.Add(mapping);
        _queue.Add(new SymbolMapping 
        {
            SourceIndex = _symbols.Count - 1,
            CodedIndex = mapping.LastIndex
        });
    }

    /// <summary>
    /// Adds <paramref name="symbol"/> to this window, fast-forwarding its mapping so the window is
    /// positioned to evaluate the stream starting at <paramref name="offset"/>.
    /// </summary>
    public void AddHashedSymbolAtOffset(HashedSymbol symbol, long offset)
    {
        var mapping = new RandomMapping(symbol.Hash, 0);

        // Fast-forward this symbol until it reaches the target offset.
        // We skip all intermediate cells because they are not needed when reconstructing window state from an offset.
        // The point is to avoid the min-heap operations, since we are not actually evaluating the intermediate cells.

        while (mapping.LastIndex < offset)
        {
            mapping.NextIndex();
        }

        // We also rewind NextIndex to 'offset' otherwise the window will evaluate 'offset' number of
        // empty cells before it starts producing (or subtracting) anything meaningful.

        NextIndex = offset;

        AddHashedSymbol(symbol, mapping);
    }

    /// <summary>
    /// Evaluates the infinite R-IBLT sequence at the exact <see cref="NextIndex"/>, applying any scheduled 
    /// source symbols into the provided coded symbol and advancing the sequence (NextIndex).
    /// </summary>
    /// <remarks>
    /// Acts as self-moving conveyor belt, because if a node as a huge number of items in its set,
    /// it is impractical to construct the "entire" R-IBLT (it is virtually "infinitely" long).
    /// </remarks>
    public CodedSymbol ApplySymbol(CodedSymbol symbol, SymbolOperation operation)
    {
        // If no symbols are tracked (cache is empty), the math evaluates to nothing.
        // In this case we just advance the NextIndex and return the coded symbol without touching it (which should be empty).

        if (_queue.Count == 0)
        {
            NextIndex++;
            return symbol;
        }

        var symbolsSpan = CollectionsMarshal.AsSpan(_symbols);
        var mappingsSpan = CollectionsMarshal.AsSpan(_mappings);

        // There can be *multiple* source symbols mapped to this exact same index (a collision).
        // The heap guarantees that the smallest CodedIndex is always at the top (the root).
        // We loop as long as the item at the top of the heap is scheduled for the *current* NextIndex.
        // Note that all source symbols will map to 0th coded symbol on the first pass.

#if RIBLT_PROFILE
        long iterations = 0;
#endif

        // Because the internal list of mappings in the queue does not resize during this loop,
        // we can safely take a reference to the root and mutate it in-place without worrying about the list moving in memory.
        // Even if the GC moves the list, the reference will still point to the same element in the list,
        // because the list itself is a ref type. This way we avoid having to Peek() on every iteration, which is a small perf win.
          
        ref var root = ref _queue.Peek(); // // We peek at the top to see which source symbol will be mapped to this cell(coded symbol).

        while (root.CodedIndex == NextIndex)
        {
#if RIBLT_PROFILE
            iterations++;
#endif
            var index = root.SourceIndex;

            // And proceed to 'apply' the source symbol (contained within hashed symbol) into the coded symbol.
            // This will perform the IBLT math: XOR'ing the source symbol's ID, and Hash into the cell,
            // and adjusting the cell's count (+1 for Add, -1 for Remove)
            symbol.Apply(symbolsSpan[index], operation);

            // Push this symbol's *next* mapping into the future.
            root.CodedIndex = mappingsSpan[index].NextIndex();

            // UpdateRoot will sift this element down and bring the next smallest item to index 0.
            // On the next loop iteration, 'root.CodedIndex' automatically evaluates the *new* minimum element!
            _queue.UpdateRoot();

#if RIBLT_PROFILE
            RibltProfiler.RecordUpdateRoot();
#endif
        }

#if RIBLT_PROFILE
        RibltProfiler.RecordProducedSymbol(iterations);
#endif

        // The current cell is now fully constructed, so we advance the sequence to the next time-step.
        NextIndex++;

        return symbol;
    }

    public void Reset()
    {
        _symbols.Clear();
        _mappings.Clear();
        _queue.Clear();
        NextIndex = 0;
    }
}

internal class RibltEncoder
{
    private readonly CodingWindow _window = new();

    public long NextIndex => _window.NextIndex;

    /// <summary>
    /// Adds <paramref name="symbol"/> to the local state, which will be used to generate coded symbols for remote peers.
    /// </summary>
    public void AddMutation(MutationSymbol symbol) => _window.AddHashedSymbol(new HashedSymbol(symbol, symbol.GetHash()));

    /// <summary>
    /// Adds <paramref name="symbol"/> to the local state, as if the encoder had already produced "<paramref name="offset"/>" number of symbols.
    /// </summary>
    public void AddMutationAtOffset(MutationSymbol symbol, long offset) => _window.AddHashedSymbolAtOffset(new HashedSymbol(symbol, symbol.GetHash()), offset);

    /// <summary>
    /// Generates an "infinite" stream of <see cref="CodedSymbol"/>s representing the local cache state.
    /// The produced coded symbols allow remote peers to solve for missing items.
    /// </summary>
    public CodedSymbol ProduceNextSymbol() => _window.ApplySymbol(default, SymbolOperation.Add);

    public void Reset() => _window.Reset();
}

/// <summary>
/// Ingests an incoming stream of <see cref="CodedSymbol"/>s.
/// It continuously subtracts the local state from the remote stream and uses a peeling algorithm 
/// to recover missing/extra items between two reconciling nodes.
/// </summary>
internal class RibltDecoder
{
    // The baseline local state. We subtract this from incoming cells to cancel out shared items.
    // This keeps the symbols that are in our local cache, or the first N from the sketch (if used).
    private readonly CodingWindow _current = new();

    // Tracks items we discovered the local node has, but the remote node is missing.
    private readonly CodingWindow _local = new();

    // Tracks items we discovered the remote node has, but the local node is missing.
    private readonly CodingWindow _remote = new();

    // Tracks a list of cells received from the remote stream, mutated in-place over time.
    private readonly List<CodedSymbol> _cells = [];

    // Tracks the indexes of the cells in the stream that are currently decodable/pure, which means ready to be processed.
    private readonly List<int> _decodableIndexes = [];

    /// <summary>
    /// If the decoder has succeeded in decoding all coded symbols. 
    /// </summary>
    public bool IsCompleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // The decoder is only fully resolved if it has processed at least one cell,
        // and all the mutations it has processed have been successfully peeled i.e. it has reached the 0th cell.
        // Reaching the 0th cell is mandatory as *every* source symbol maps to the first coded symbol (index 0) on its first encoding pass.
        get => _cells.Count > 0 && _cells[0].IsEmpty;
    }

    /// <summary>
    /// Symbols present locally, but missing remotely.
    /// </summary>
    public IReadOnlyList<HashedSymbol> LocalSymbols => _local.Symbols;

    /// <summary>
    /// Symbols present remotely, but missing locally.
    /// </summary>
    public IReadOnlyList<HashedSymbol> RemoteSymbols => _remote.Symbols;

    /// <summary>
    /// Adds a new mutation to the baseline local state, which will be subtracted from incoming coded symbols.
    /// </summary>
    public void AddMutation(MutationSymbol symbol) => _current.AddHashedSymbol(new HashedSymbol(symbol, symbol.GetHash()));

    /// <summary>
    /// Adds <paramref name="symbol"/> to the baseline local state, as if the baseline window had
    /// already consumed "<paramref name="offset"/>" number of incoming stream symbols.
    /// </summary>
    public void AddMutationAtOffset(MutationSymbol symbol, long offset) => _current.AddHashedSymbolAtOffset(new HashedSymbol(symbol, symbol.GetHash()), offset);

    /// <summary>
    /// Ingests a new cell from the remote peer's stream, neutralizes known data, and checks if it is ready to be peeled.
    /// </summary>
    public void ApplySymbol(CodedSymbol symbol)
    {
        // First we subtract the baseline local state. If two nodes both share an item, 
        // subtracting it here causes it to cancel out to 0.
        symbol = _current.ApplySymbol(symbol, SymbolOperation.Remove);

        // We also subtract any remote items we *already* have peeled in previous runs.
        symbol = _remote.ApplySymbol(symbol, SymbolOperation.Remove);

        // We add back any local items we *already* have peeled in previous runs.
        // Note that we add instead of subtract because local items are represented by -1 in the CodedSymbol.Count
        symbol = _local.ApplySymbol(symbol, SymbolOperation.Add);

        _cells.Add(symbol);

        // If the cell arrives pure (Count == 1 || -1), or if it arrived empty, we queue it for the peeling algorithm.
        if (symbol.IsDecodable || symbol.IsEmpty)
        {
            _decodableIndexes.Add(_cells.Count - 1);
        }
    }

    /// <summary>
    /// Bypasses the baseline subtraction phase, as the caller has already neutralized overlapping state. 
    /// It applies existing peeled mutations to the symbol, and queues it for the peeling cascade if it becomes decodable.
    /// </summary>
    /// <remarks>
    /// Bypasses the O(N) baseline _current subtraction.
    /// Should be used only when the sketch is used.
    /// </remarks>
    public void ApplyDifferenceSymbol(CodedSymbol diffSymbol)
    {
        // We still subtract items we already peeled in previous cascade iterations!
        diffSymbol = _remote.ApplySymbol(diffSymbol, SymbolOperation.Remove);
        diffSymbol = _local.ApplySymbol(diffSymbol, SymbolOperation.Add);

        _cells.Add(diffSymbol);

        if (diffSymbol.IsDecodable || diffSymbol.IsEmpty)
        {
            _decodableIndexes.Add(_cells.Count - 1);
        }
    }

    /// <summary>
    /// Attempts to fully decode the stream of <see cref="CodedSymbol"/>s received from 
    /// <see cref="ApplySymbol(CodedSymbol)"/> / <see cref="ApplyDifferenceSymbol(CodedSymbol)"/>,
    /// resolving pure cells, applying their symbols to inpure/mixed cells, which crates new pure cells, continuing until exhaustion.
    /// </summary>
    public void TryDecode()
    {
        var span = CollectionsMarshal.AsSpan(_cells);

        for (int i = 0; i < _decodableIndexes.Count; i++)
        {
            int cellIndex = _decodableIndexes[i];
            ref var codedSymbol = ref span[cellIndex];

            switch (codedSymbol.Count)
            {
                case 0: // Count = 0 means its empty, so nothing to extract.
                    break;
                case 1:
                    {
                        // Count == 1 means the remote has this item, but I dont have it.
                        // Because the cell is pure, the SymbolSum IS the actual MutationSymbol.Id
                        // We extract it by XOR'ing it with an empty default struct.

                        var hashedSymbol = new HashedSymbol(default(MutationSymbol).Xor(codedSymbol.SymbolSum), codedSymbol.CheckSum);

                        // We sweep through all previously received cells and remove this item from them.
                        var mapping = ApplyNewSymbol(hashedSymbol, SymbolOperation.Remove, span);

                        // Lastly we save it so we can apply it to future cells received in later rounds.
                        _remote.AddHashedSymbol(hashedSymbol, mapping);

                        break;
                    }
                case -1:
                    {
                        // Count == -1 means this is an item I have, but the remote node is missing.
                        // Again the cell is pure, and we apply the same XOR'ing but this time we add this item
                        // back into the previpusly recevied cells, and save it so we can apply it to future cells.

                        var hashedSymbol = new HashedSymbol(default(MutationSymbol).Xor(codedSymbol.SymbolSum), codedSymbol.CheckSum);
                        var mapping = ApplyNewSymbol(hashedSymbol, SymbolOperation.Add, span);

                        _local.AddHashedSymbol(hashedSymbol, mapping);

                        break;
                    }
                default:
                    throw new UnreachableException($"Cell was marked as decodable, but Count is = {codedSymbol.Count}");
            }
        }

        // We are done with this round of decoding, we we prepare for another round if it comes.
        _decodableIndexes.Clear();
    }

    /// <summary>
    /// Simulates the PRNG jumps for a newly discovered symbol, applying it to all cells we have received so far.
    /// </summary>
    private RandomMapping ApplyNewSymbol(HashedSymbol hashedSymbol, SymbolOperation operation, Span<CodedSymbol> cells)
    {
        var mapping = new RandomMapping(hashedSymbol.Hash, 0);

        // We only care about applying this to cells we have received so far.
        while (mapping.LastIndex < cells.Length)
        {
            // We can explicitly cast LastIndex to int because of the loop bounds check, since cells.Length is an int
            int cellIndex = (int)mapping.LastIndex;

            cells[cellIndex].Apply(hashedSymbol, operation);

            // If by applying this peeled symbol to a mixed cell caused it to become pure,
            // we queue it into _decodableIndexes so the TryDecode can process it.
            if (cells[cellIndex].IsDecodable)
            {
                _decodableIndexes.Add(cellIndex);
            }

            // Lastly we calculate the symbol's next jump in the rateless sequence.
            mapping.NextIndex();
        }

        return mapping;
    }
}