namespace Ledjon.ReplicatedMemoryCache;

/// <summary>
/// A buffer of <see cref="CodedSymbol"/>s that is materialized for fast access for small(ish) set differences.
/// </summary>
/// <remarks>
/// Since R-IBLT are linear, we dont need to re-compute the prefix on each access.
/// We can just apply the mutations directly to the materialized buffer in O(log N) time.
/// This is very much like a fixed-size, regular IBTL, just that it has no k-hash functions
/// to pick the random cells, but the cells are picked via the PRNG generator functions.
/// </remarks>
internal class RibltSketch
{
    private const int LockCount = 64; // MUST be a power of 2 for bitwise masking.
    private const int LockMask = LockCount - 1;

    /// <summary>
    /// <para>
    /// The <see cref="LocalNodeCache"/> uses lock striping based on the <b>cache key</b> to prevent 
    /// concurrent updates to the same cache entry. However, 2 threads updating completely <b>different cache keys</b>
    /// (holding different cache locks) might still have their PRNG sequences overlap and land on the exact same 
    /// cell index in this sketch at the same time.
    /// </para>
    /// <para>
    /// The number of locks (64) is chosen intentionally much lower than the local node cache (1024), 
    /// because the critical section inside these locks is executed very fast (just some XOR math on CodedSymbol.Apply(hashedSymbol, operation)).
    /// Because threads enter and exit this section rapidly, and because the PRNG scatters writes pseudo-randomly across the array,
    /// 64 locks heavily dilutes the probability of contention on the same cell index at any point in time.
    /// </para>
    /// </remarks>
    private readonly Lock[] _lockStrip = [.. Enumerable.Range(0, LockCount).Select(_ => new Lock())];

    private readonly CodedSymbol[] _buffer;

    public int Size => _buffer.Length;

    public RibltSketch(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1, nameof(size));

        _buffer = new CodedSymbol[size];
    }

    /// <summary>
    /// Maps the mutation symbol across the sketch using pseudo-random indices derived from its hash.
    /// </summary>
    /// <remarks>
    /// <param name="symbol">The mutation symbol to apply.</param>
    /// <param name="operation">The operation to perform (add/remove)</param>
    public void ApplyMutation(MutationSymbol symbol, SymbolOperation operation)
    {
        var hashedSymbol = new HashedSymbol(symbol, symbol.GetHash());
        var mapping = new RandomMapping(hashedSymbol.Hash, 0);

        while (mapping.LastIndex < _buffer.Length)
        {
            int index = (int)mapping.LastIndex;
            var lockObj = _lockStrip[index & LockMask];

            lock (lockObj)
            {
                // We lock here to prevent lost updates during the RMW cycle.
                // Applying a symbol involves non-atomic XOR and addition operations across 32 bytes (CodedSymbol size).
                // If two separate cache updates PRNG-collide on this exact cell simultaneously without a lock,
                // their XORs would interleave and permanently corrupt the cell's math.
                _buffer[index].Apply(hashedSymbol, operation);
            }

            mapping.NextIndex();
        }
    }

    /// <summary>
    /// Reads a slice of the sketch safely into the provided destination span.
    /// </summary>
    /// <returns>The number of symbols read.</returns>
    public int ReadSlice(int offset, Span<CodedSymbol> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var resultCount = Math.Min(destination.Length, _buffer.Length - offset); // To ensure we dont read out of bounds!
        if (resultCount <= 0)
        {
            return 0;
        }

        for (int i = 0; i < resultCount; i++)
        {
            int index = offset + i;
            var lockObj = _lockStrip[index & LockMask];

            lock (lockObj)
            {
                // We must lock the read to prevent a torn-read since CodedSymbol is 32 bytes (CPU cant copy it atomically).
                // If we dont lock, a concurrent write could alter the cell mid-copy, resulting in a mathematically corrupted symbol.
                // This could happen when a reconcilliation/gossip thread tries to read a slice,
                // at the exact same time a cache thread is updating that same cell.

                // Since we lock, we can copy the 32-byte struct into the caller's span.
                // This guarantees the caller gets a valid snapshot of the cell, without allocating on the heap.

                destination[i] = _buffer[index];
            }
        }

        return resultCount;
    }
}