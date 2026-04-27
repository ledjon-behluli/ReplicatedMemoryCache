using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.TestingHost;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Ledjon.ReplicatedMemoryCache.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class SequentialTestsCollection
{
    public const string Name = "SequentialTests";
}

public record TestState(FakeTimeProvider Time, ReplicatedMemoryCacheOptions Options);

public static class TestStateRegistry
{
    private static readonly ConcurrentDictionary<string, TestState> States = new();

    public static void Register(string id, FakeTimeProvider time, ReplicatedMemoryCacheOptions options)
        => States[id] = new TestState(time, options);

    public static TestState Get(string id) => States[id];
    public static void Remove(string id) => States.TryRemove(id, out _);
}

public static class TestHelpers
{
    public static async Task<IHost> CreateClientAsync<TSerializer>(
        TestCluster cluster, ReplicatedMemoryCacheOptions testOptions, TimeProvider timeProvider, string[] scopeNames)
            where TSerializer : class, ICacheEntrySerializer
    {
        var host = new HostBuilder().UseOrleansClient((ctx, clientBuilder) =>
        {
            clientBuilder.Configure<ClusterOptions>(options =>
            {
                options.ClusterId = cluster.Options.ClusterId;
                options.ServiceId = cluster.Options.ServiceId;
            });

            clientBuilder.UseStaticClustering([.. cluster.Silos.Select(s => s.GatewayAddress.Endpoint)]);
            clientBuilder.Services.AddSingleton(timeProvider);

            ConfigureCacheReplication<TSerializer>(clientBuilder.Services, testOptions, scopeNames);
        }).Build();

        await host.StartAsync();

        return host;
    }

    public static void ConfigureCacheReplication<TSerializer>(
        IServiceCollection services, ReplicatedMemoryCacheOptions testOptions, string[] scopeNames)
            where TSerializer : class, ICacheEntrySerializer
    { 
        foreach (var scopeName in scopeNames)
        {
            Action<ReplicatedMemoryCacheOptions> configureOptions = options =>
            {
                options.ReconciliationPeriod = testOptions.ReconciliationPeriod;
                options.NodeLivenessPeriod = testOptions.NodeLivenessPeriod;
                options.MemorySweepingPeriod = testOptions.MemorySweepingPeriod;
                options.ExpiredMutationGracePeriod = testOptions.ExpiredMutationGracePeriod;
                options.MissedHeartbeatsBeforeIndirectProbing = testOptions.MissedHeartbeatsBeforeIndirectProbing;
                options.MaxSymbolsToPull = testOptions.MaxSymbolsToPull;
                options.SymbolStreamBatchSize = testOptions.SymbolStreamBatchSize;
                options.MutationTransferBatchSize = testOptions.MutationTransferBatchSize;
                options.MaterializedSketchSize = testOptions.MaterializedSketchSize;
            };

            if (typeof(TSerializer) == typeof(DefaultSerializerMarker))
            {
                services.AddMemoryCacheReplication(scopeName, configureOptions);
            }
            else
            {
                services.AddMemoryCacheReplication<TSerializer>(scopeName, configureOptions);
            }
        }
        ;
    }

    public static List<CacheConsumerNode> GetRandomNodes(
        TestCluster cluster, List<IHost> hosts, int count, string scopeName)
    {
        var nodes = new List<CacheConsumerNode>();

        foreach (var siloHandle in cluster.Silos)
        {
            var sp = cluster.GetSiloServiceProvider(siloHandle.SiloAddress);
            nodes.Add(new(sp.GetRequiredKeyedService<IReplicatedMemoryCache>(scopeName)));
        }

        foreach (var host in hosts)
        {
            nodes.Add(new(host.Services.GetRequiredKeyedService<IReplicatedMemoryCache>(scopeName)));
        }

        return [.. nodes.OrderBy(_ => Guid.NewGuid()).Take(count)];
    }

    public static async Task WaitForGossipPropagationAsync(FakeTimeProvider fakeTime, TimeSpan reconciliationPeriod)
    {
        const int GossipRounds = 10;

        // 10 rounds is virtually guaranteed to converge a 5-node cluster (see simulations project).

        for (int i = 0; i < GossipRounds; i++)
        {
            await Task.Delay(reconciliationPeriod);
            fakeTime.Advance(reconciliationPeriod);
        }
    }
}

public class CacheConsumerNode(IReplicatedMemoryCache cache)
{
    public IReplicatedMemoryCache Cache => cache;

    public void Remove(string key) => cache.Remove(key);

    public string? Get(string key)
    {
        cache.TryGetValue(key, out string? value);
        return value;
    }

    public void Set(string key, string value, TimeSpan? ttl = null)
    {
        if (ttl.HasValue && ttl.Value == TimeSpan.Zero)
        {
            cache.Remove(key);
            return;
        }

        if (ttl.HasValue)
        {
            cache.Set(key, value, ttl.Value);
        }
        else
        {
            cache.Set(key, value);
        }
    }
}

public class DefaultSerializerMarker : ICacheEntrySerializer
{
    public byte[] Serialize(object obj) => throw new InvalidOperationException("Marker type only.");
    public object? Deserialize(byte[] bytes) => throw new InvalidOperationException("Marker type only.");
}

public class SystemTextJsonCacheEntrySerializer : ICacheEntrySerializer
{
    public byte[] Serialize(object obj)
    {
        var wrapper = new TypeWrapper
        {
            TypeName = obj.GetType().AssemblyQualifiedName!,
            Payload = JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetType())
        };

        return JsonSerializer.SerializeToUtf8Bytes(wrapper);
    }

    public object? Deserialize(byte[] bytes)
    {
        if (JsonSerializer.Deserialize<TypeWrapper>(bytes) is not { } wrapper)
        {
            return null;
        }

        var type = Type.GetType(wrapper.TypeName);
        if (type == null)
        {
            return null;
        }

        return JsonSerializer.Deserialize(wrapper.Payload, type);
    }

    public class TypeWrapper
    {
        public string TypeName { get; set; } = "";
        public byte[] Payload { get; set; } = [];
    }
}