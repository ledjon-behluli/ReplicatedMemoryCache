using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Ledjon.ReplicatedMemoryCache;

internal class LocalNodeCache(TimeProvider timeProvider, ReplicatedMemoryCacheOptions options)
{
    private sealed record CacheEntry(byte[]? Payload, VersionTag Version, bool IsTombstone, DateTimeOffset? ExpiresAt, Guid MutationId);

    private readonly VersionSource _source = new(Guid.NewGuid().GetHashCode());
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<Guid, CacheMutation> _mutations = new();
    private readonly TimeSpan _mutationGracePeriod = options.ExpiredMutationGracePeriod;

    private const int LockCount = 1024; // MUST be a power of 2 for bitwise masking.
    private const int LockMask = LockCount - 1;

    private readonly Lock[] _lockStrip = [.. Enumerable.Range(0, LockCount).Select(_ => new Lock())];

    public IEnumerable<CacheMutation> Mutations => _mutations.Values;

    public RibltSketch? Sketch { get; private set; } = options.MaterializedSketchSize > 0 ? new(options.MaterializedSketchSize) : null;
    
    public CacheMutation? GetMutation(Guid id) => _mutations.TryGetValue(id, out var mutation) ? mutation : null;

    /// <summary>
    /// Applies <paramref name="action"/> to all mutations local to this node.
    /// </summary>
    public void ForEachMutation<TState>(TState state, Action<TState, MutationSymbol> action)
    {
        // Instead of the encoder/decoder using the keys of the dict (which are the mutation ids),
        // we iterate over the dict directly to avoid boxing the Guid keys, and apply the action to each.
        // This is more efficient for large dictionaries, and I expect the number of mutations to be large in a busy cache.

        foreach (var kvp in _mutations)
        {
            action.Invoke(state, new MutationSymbol(kvp.Key));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Lock GetLock(string key) => _lockStrip[(uint)key.GetHashCode() & LockMask];

    public byte[]? Get(string key)
    {
        if (_cache.TryGetValue(key, out var entry) && !entry.IsTombstone)
        {
            if (entry.ExpiresAt.HasValue && entry.ExpiresAt.Value <= timeProvider.GetUtcNow())
            {
                // This is a logical eviction, meaning the entry is here but its time is up!
                // We dont need to remove the entry as the period sweep will take care of it.

                return null;
            }

            return entry.Payload;
        }

        return null;
    }

    public void Set(string key, byte[] value, TimeSpan? ttl = null)
    {
        var utcNow = timeProvider.GetUtcNow();

        ApplyMutation(new CacheMutation
        {
            MutationId = Guid.NewGuid(),
            Version = _source.GetNextVersion(utcNow.Ticks),
            CacheKey = key,
            IsTombstone = false,
            PayloadBytes = value,
            PayloadHash = value.Length > 0 ? StableHash.ComputeHash(value) : 0,
            ExpiresAt = ttl.HasValue ? utcNow + ttl.Value : null
        });
    }

    public void Remove(string key) => ApplyMutation(new CacheMutation
    {
        MutationId = Guid.NewGuid(),
        Version = _source.GetNextVersion(timeProvider.GetUtcNow().Ticks),
        CacheKey = key,
        IsTombstone = true,
        PayloadBytes = null,
        PayloadHash = 0,
        ExpiresAt = null
    });

    public void ApplyMutation(CacheMutation mutation)
    {
        if (mutation.ExpiresAt.HasValue && timeProvider.GetUtcNow() > mutation.ExpiresAt.Value + _mutationGracePeriod)
        {
            // It can be that lagging node applies the mutation but it is already objectively expired
            // Like, the sweeping has already occurred in this (receiver) node, so we wont re-add it!
            return;
        }

        // If this mutation came from a node whose clock is ahead of ours, we artificially advance our clock to match it.
        _source.CatchUp(mutation.Version.Ticks);

        var cacheKey = mutation.CacheKey;

        lock (GetLock(cacheKey))
        {
            if (_cache.TryGetValue(cacheKey, out var existing))
            {
                if (mutation.Version <= existing.Version)
                {
                    // If the incoming mutation is older or a duplicate, its safe to discard it.
                    return;
                }

                // To prevent a memory leak, we are overwriting the key, so we MUST remove the old mutation id from the journal.
                _mutations.TryRemove(existing.MutationId, out _);

                // We also remove the old mutation from the sketch, so that it is not sent to other nodes anymore.
                Sketch?.ApplyMutation(new MutationSymbol(existing.MutationId), SymbolOperation.Remove);
            }

            // Then we atomically update both collections!
            _mutations[mutation.MutationId] = mutation;
            _cache[cacheKey] = new CacheEntry(mutation.PayloadBytes, mutation.Version, mutation.IsTombstone, mutation.ExpiresAt, mutation.MutationId);

            // We also add the new mutation to the sketch, so that it is sent to other nodes.
            Sketch?.ApplyMutation(new MutationSymbol(mutation.MutationId), SymbolOperation.Add);
        }
    }

    public void SweepExpiredMutations()
    {
        var utcNow = timeProvider.GetUtcNow();

        // We only need to iterate over _cache because every valid mutation is tracked there.
        foreach (var kvp in _cache)
        {
            var key = kvp.Key;

            lock (GetLock(key))
            {
                // Need to double-check the state inside the lock to ensure it hasnt been updated 
                // by a user shortly before the sweep got triggered.

                if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt.HasValue)
                {
                    if (utcNow > entry.ExpiresAt.Value + _mutationGracePeriod)
                    {
                        // We can safely remove both, without risking a race conditions with ongoing reads/writes.
                        _cache.TryRemove(key, out _);
                        _mutations.TryRemove(entry.MutationId, out _);
                        // We also remove the old mutation from the sketch, so that it is not sent to other nodes anymore.
                        Sketch?.ApplyMutation(new MutationSymbol(entry.MutationId), SymbolOperation.Remove);
                    }
                }
            }
        }
    }
}