using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Orleans.TestingHost;

namespace Ledjon.ReplicatedMemoryCache.Tests;

[Collection(SequentialTestsCollection.Name)]
public class IsolationTests_UsingDefaultSerializer : IsolationTestBase<DefaultSerializerMarker> { }

[Collection(SequentialTestsCollection.Name)]
public class IsolationTests_UsingSystemTextJsonSerializer : IsolationTestBase<SystemTextJsonCacheEntrySerializer> { }

public abstract class IsolationTestBase<TSerializer> : IAsyncLifetime
    where TSerializer : class, ICacheEntrySerializer
{
    private TestCluster _cluster = null!;
    private readonly List<IHost> _clients = [];
    private readonly string _testId = Guid.NewGuid().ToString("N");

    private const string ScopeA = "scope_A";
    private const string ScopeB = "scope_B";

    private readonly FakeTimeProvider _fakeTime = new();
    private readonly ReplicatedMemoryCacheOptions _testOptions = new()
    {
        ReconciliationPeriod = TimeSpan.FromMilliseconds(100),
        ExpiredMutationGracePeriod = TimeSpan.FromSeconds(1),
        MemorySweepingPeriod = TimeSpan.FromSeconds(2)
    };

    public async Task InitializeAsync()
    {
        TestStateRegistry.Register(_testId, _fakeTime, _testOptions);

        var builder = new TestClusterBuilder(initialSilosCount: 3);

        builder.Properties["TestId"] = _testId;
        builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;

        _cluster = builder.AddSiloBuilderConfigurator<TestSiloConfigurator>().Build();

        await _cluster.DeployAsync();

        var client1Task = TestHelpers.CreateClientAsync<TSerializer> (_cluster, _testOptions, _fakeTime, [ScopeA, ScopeB]);
        var client2Task = TestHelpers.CreateClientAsync<TSerializer>(_cluster, _testOptions, _fakeTime, [ScopeA, ScopeB]);

        await Task.WhenAll(client1Task, client2Task);

        _clients.Add(await client1Task);
        _clients.Add(await client2Task);
    }

    private class TestSiloConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices((context, services) =>
            {
                var state = TestStateRegistry.Get(context.Configuration["TestId"]!);

                services.AddSingleton<TimeProvider>(state.Time);

                TestHelpers.ConfigureCacheReplication<TSerializer>(services, state.Options, [ScopeA, ScopeB]);
            });
        }
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_clients.Select(host => host?.StopAsync() ?? Task.CompletedTask));
        await (_cluster?.StopAllSilosAsync() ?? Task.CompletedTask);

        TestStateRegistry.Remove(_testId);
    }

    [Fact]
    public async Task LocalMemory_IsStrictlyIsolated_BetweenScopesOnSameNode()
    {
        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 1, ScopeA);

        // We grab Scope A and B from the same client node.
        // We know clients[0] is the same node, so we pull its ScopeB cache explicitly.

        var node_ScopeA = nodes[0];
        var node_ScopeB = new CacheConsumerNode(_clients[0].Services.GetRequiredKeyedService<IReplicatedMemoryCache>(ScopeB));

        var key = "key";

        node_ScopeA.Set(key, "val");

        // It should be immediately available in scope A, but missing in B.
        Assert.Equal("val", node_ScopeA.Get(key));
        Assert.Null(node_ScopeB.Get(key));
    }

    [Fact]
    public async Task SameKey_DifferentValues_DoNotCollideOrOverwriteAcrossScopes()
    {
        var nodesA = TestHelpers.GetRandomNodes(_cluster, _clients, 2, ScopeA);
        var nodesB = TestHelpers.GetRandomNodes(_cluster, _clients, 2, ScopeB);

        var node1_ScopeA = nodesA[0];
        var node2_ScopeA = nodesA[1];
        var node3_ScopeB = nodesB[0];
        var node4_ScopeB = nodesB[1];

        var key = "key";

        // Different scopes mutate the exact same key at the exact same time.
        node1_ScopeA.Set(key, "val_A");
        node3_ScopeB.Set(key, "val_B");

        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        // Replicaiton handles them as two completely parallel things.
        Assert.Equal("val_A", node2_ScopeA.Get(key));
        Assert.Equal("val_B", node4_ScopeB.Get(key));
    }

    [Fact]
    public async Task EpidemicGossip_DoesNotLeak_AcrossScopeBoundaries()
    {
        var nodesA = TestHelpers.GetRandomNodes(_cluster, _clients, 2, ScopeA);

        var node1_ScopeA = nodesA[0];
        var node2_ScopeA = nodesA[1];

        // We need to find the equivalent Scope B caches on both nodes to prove network isolation
        CacheConsumerNode node1_ScopeB = FindEquivalent_ScopeB_Node(node1_ScopeA);
        CacheConsumerNode node2_ScopeB = FindEquivalent_ScopeB_Node(node2_ScopeA);
        
        var key = "key";

        // Node 1 mutates the network on Scope A.
        node1_ScopeA.Set(key, "val");

        // The write on Node 1 is local in Scope A, but Scope B must remain completely unaware.
        Assert.Equal("val", node1_ScopeA.Get(key));
        Assert.Null(node1_ScopeB.Get(key));

        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        // The write on Node 2 should have been replicated on Scope A, but Scope B must remain completely unaware.
        Assert.Equal("val", node2_ScopeA.Get(key));
        Assert.Null(node2_ScopeB.Get(key));

        CacheConsumerNode FindEquivalent_ScopeB_Node(CacheConsumerNode scopeA_Node)
        {
            CacheConsumerNode? scopeB_Node = null;

            if (_clients.Any(c => c.Services.GetRequiredKeyedService<IReplicatedMemoryCache>(ScopeA) == scopeA_Node.Cache))
            {
                var host = _clients.First(c => c.Services.GetRequiredKeyedService<IReplicatedMemoryCache>(ScopeA) == scopeA_Node.Cache);
                scopeB_Node = new CacheConsumerNode(host.Services.GetRequiredKeyedService<IReplicatedMemoryCache>(ScopeB));
            }
            else
            {
                var siloHandle = _cluster.Silos.First(handle =>
                    _cluster.GetSiloServiceProvider(handle.SiloAddress)
                            .GetRequiredKeyedService<IReplicatedMemoryCache>(ScopeA) == scopeA_Node.Cache);

                scopeB_Node = new CacheConsumerNode(_cluster
                    .GetSiloServiceProvider(siloHandle.SiloAddress)
                    .GetRequiredKeyedService<IReplicatedMemoryCache>(ScopeB));
            }

            return scopeB_Node;
        }
    }
}