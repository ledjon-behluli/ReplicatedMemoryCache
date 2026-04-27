using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Ledjon.ReplicatedMemoryCache;

/// <summary>
/// Represents an <see cref="IMemoryCache"/> implementation whose entries are replicated across multiple nodes.
/// This cache preserves the standard <see cref="IMemoryCache"/> programming model while synchronizing mutations 
/// through a replication layer so that cached values become visible cluster-wide.
/// </summary>
/// <remarks>
/// <b>This cache supports only <see cref="string"/> keys, and does not honor the <see cref="ICacheEntry.SlidingExpiration"/>, 
/// instead it is treated as <see cref="ICacheEntry.AbsoluteExpirationRelativeToNow"/>. 
/// This is because implementing sliding expiration in a distributed cache would require network gossip
/// on every single read to update the TTL across all nodes, therefore defeating the purpose of 0-hop reads.</b>
/// </remarks>
public interface IReplicatedMemoryCache : IMemoryCache;

internal class ReplicatedMemoryCache(TimeProvider timeProvider, LocalNodeCache localCache,  ICacheEntrySerializer serializer) : IReplicatedMemoryCache
{
    public bool TryGetValue(object key, out object? value)
    {
        value = null;

        var strKey = GetOrThrowIfInvalidKey(key);
        var payloadBytes = localCache.Get(strKey);

        if (payloadBytes is not { Length: > 0 } bytes)
        {
            return false;
        }

        value = serializer.Deserialize(bytes);

        return true;
    }

    public ICacheEntry CreateEntry(object key)
    {
        var strKey = GetOrThrowIfInvalidKey(key);
        return new CacheEntry(strKey, timeProvider, localCache, serializer);
    }

    public void Remove(object key)
    {
        var strKey = GetOrThrowIfInvalidKey(key);
        localCache.Remove(strKey);
    }

    public void Dispose() => GC.SuppressFinalize(this);

    private static string GetOrThrowIfInvalidKey(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key as string ?? ThrowInvalidKey();
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)] // To keep the exception path out of hot call sites!
    private static string ThrowInvalidKey()
    {
        throw new ArgumentException("Cache keys must be strings only!");
    }

    /// <summary>
    /// Represents a pending cache operation. When Dispose() is called, which 
    /// happens implicitly at the end of cache.Set(), the mutation is pushed to the journal.
    /// </summary>
    private class CacheEntry(string key,
        TimeProvider timeProvider, LocalNodeCache localCache,
        ICacheEntrySerializer serializer) : ICacheEntry
    {
        private bool _isCommitted;

        public object Key { get; } = key;
        public object? Value { get; set; }

        public DateTimeOffset? AbsoluteExpiration { get; set; }
        public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }
        public TimeSpan? SlidingExpiration { get; set; }

        public IList<IChangeToken> ExpirationTokens { get; } = [];
        public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } = [];
        public CacheItemPriority Priority { get; set; } = CacheItemPriority.Normal;
        public long? Size { get; set; }

        public void Dispose()
        {
            if (_isCommitted)
            {
                return;
            }

            _isCommitted = true;

            if (Value is null)
            {
                localCache.Remove((string)Key);
                return;
            }

            TimeSpan? ttl = null;

            if (AbsoluteExpirationRelativeToNow.HasValue)
            {
                ttl = AbsoluteExpirationRelativeToNow.Value; // AbsoluteExpirationRelativeToNow is already the TTL
            }
            else if (AbsoluteExpiration.HasValue)
            {
                ttl = AbsoluteExpiration.Value - timeProvider.GetUtcNow();
            }
            else if (SlidingExpiration.HasValue)
            {
                // SlidingExpiration is treat as AbsoluteExpirationRelativeToNow here! In a distributed cache, sliding expiration
                // would require network gossip on every single read to update the TTL across all nodes, therefore defeating the
                // purpose of 0-hop reads.
                ttl = SlidingExpiration.Value;
            }

            if (ttl.HasValue && ttl.Value <= TimeSpan.Zero)
            {
                // If the user explicitly sets a zero TTL, we treat it as an explicit Remove/Tombstone.
                localCache.Remove((string)Key);
                return;
            }

            localCache.Set((string)Key, serializer.Serialize(Value), ttl);
        }
    }
}