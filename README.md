# ReplicatedMemoryCache

A distributed, eventually consistent, zero-hop reads memory cache for .NET applications running on [Microsoft Orleans](https://github.com/dotnet/orleans). It preserves the standard `IMemoryCache` programming model while synchronizing caches across the cluster.

[![NuGet](https://img.shields.io/nuget/v/ReplicatedMemoryCache.svg)](https://www.nuget.org/packages/ReplicatedMemoryCache/)

> You can read more behind the math [here](https://www.ledjonbehluli.com/posts/orleans_riblt_replicated_memory_cache/).

## Capabilities

-   **Zero-Hop Reads:** Because cache state is continuously replicated to the local memory of every node, cache reads incur zero network latency and deserialization overhead.
    
-   **Epidemic Gossip Protocol:** Nodes periodically pick random peers to reconcile the global view of the cache instead of requiring a central leader.
    
-   **Efficient Synchronization:** This packages uses [Rateless Set Reconciliation](https://arxiv.org/pdf/2402.02668) so that nodes calculate the exact difference between their caches and send the difference over the network making the payloads extremely small.
    
-   **Named Scopes:** The library supports scoped-memories by name. You can run multiple isolated distributed caches within the same application, each with its own configuration.

## Registration

By default, the library registers the cache under the `IReplicatedMemoryCache` interface. This extends `IMemoryCache`, but it is **not** registered as the default implementation for `IMemoryCache` in the service collection. 

```csharp
// Recommended Registration
services.AddReplicatedMemoryCache(scopeName: "default", options => { ... });
```

### Why not `IMemoryCache` by default?

Several .NET components, including EF Core and Output Caching use in-memory cache implementations for internal state tracking. Unintentionally replicating those caches across your entire cluster very likely is not what you the user would want. If your specific architecture requires it, or you just want a drop-in replacement AS-IS, you can explicitly override the global `IMemoryCache` registration to serve the replicated cache:

```csharp
// Global Override! Caution as this replicates everything that uses IMemoryCache.
services.AddReplicatedMemoryCache(scopeName: "global", options => { ... });
services.AddSingleton<IMemoryCache>(sp => sp.GetRequiredKeyedService<IReplicatedMemoryCache>("global"));
```

## Usage

```csharp
public class CacheConsumer([FromKeyedServices("default")] IReplicatedMemoryCache cache)
{
    public Product? GetProduct(string productId)
    {
        // Evaluates instantly against local memory, zero-hop!
        if (cache.TryGetValue(productId, out Product? product))
        {
            return product;
        }

        return null;
    }

    public void UpdateProduct(string productId, Product product)
    {
        // Pushes the update to the local journal.
        cache.Set(productId, product, TimeSpan.FromHours(1));
    }
}

```

## Serialization

Because this library is built on top of Orleans, it leverages the highly optimized serializer it ships by default. Any custom objects you place into the cache must be decorated with standard Orleans serialization attributes so the underlying protocol can properly encode, replicate, and decode your cache over the network. If you need to cache 3rd party types that you do not own and can not decorate with the Orleans attributes, you can swap out the serializer.

```csharp
services.AddReplicatedMemoryCache<CustomCacheEntrySerializer>(scopeName: "default", options => { ... });

public class CustomCacheEntrySerializer : ICustomCacheEntrySerializer
{
    public byte[] Serialize(object obj) { /* implement */ }
    public object? Deserialize(byte[] bytes) { /* implement */ }
}
```
<br/>

> Because the caching engine operates on raw `object` types, formatters like `System.Text.Json` require you to preserve the exact type information during serialization so it can be accurately restored on remote nodes. You can achieve this by wrapping the payload, see the [test](https://github.com/ledjon-behluli/ReplicatedMemoryCache/blob/main/ReplicatedMemoryCache.Tests/Helpers.cs) project on how to achieve this.

## Constraints

-   **String Keys Only:** The replication engine requires keys to be of `string` type for deterministic hashing and reconciliation. Attempting to use non-string keys will throw an `ArgumentException`.
    
-   **No Sliding Expiration:** The distributed nature of the cache does **not** honor `ICacheEntry.SlidingExpiration`. Implementing sliding expiration would require a network broadcast on every single read operation, thereby entirely defeating the purpose of 0-hop reads. Any `SlidingExpiration` provided is treated as an `AbsoluteExpirationRelativeToNow`.
