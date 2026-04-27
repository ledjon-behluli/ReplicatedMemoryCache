using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Orleans.TestingHost;

namespace Ledjon.ReplicatedMemoryCache.Tests;

[Collection(SequentialTestsCollection.Name)]
public class ReplicationTests_WithoutSketch_UsingDefaultSerializer : ReplicationTests_WithoutSketch<DefaultSerializerMarker> { }

[Collection(SequentialTestsCollection.Name)]
public class ReplicationTests_WithoutSketch_UsingSystemTextJsonSerializer : ReplicationTests_WithoutSketch<SystemTextJsonCacheEntrySerializer> { }

[Collection(SequentialTestsCollection.Name)]
public class ReplicationTests_WithSketch_UsingDefaultSerializer : ReplicationTests_WithSketch<DefaultSerializerMarker> { }

[Collection(SequentialTestsCollection.Name)]
public class ReplicationTests_WithSketch_UsingSystemTextJsonSerializer : ReplicationTests_WithSketch<SystemTextJsonCacheEntrySerializer> { }

public abstract class ReplicationTests_WithoutSketch<TSerializer> : ReplicationTestsBase<TSerializer>
    where TSerializer : class, ICacheEntrySerializer
{
    protected ReplicationTests_WithoutSketch() : base(new ReplicatedMemoryCacheOptions
    {
        MaterializedSketchSize = 0, // Forces the fast-path to be disabled entirely.
        ReconciliationPeriod = TimeSpan.FromMilliseconds(100),
        ExpiredMutationGracePeriod = TimeSpan.FromSeconds(1),
        MemorySweepingPeriod = TimeSpan.FromSeconds(2)
    }) { }

    [Fact]
    public async Task Encoder_HandlesBulkHydration_WhenSketchIsDisabled()
    {
        // We use 500 items because it is large enough to verify multi-batch synchronization,
        // but small enough to run quickly without needing sketch capacity constraints.

        const int ItemCount = 500;

        Assert.Equal(0, _testOptions.MaterializedSketchSize);

        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 1, TestScope);
        var existingNode = nodes[0];

        for (int i = 0; i < ItemCount; i++)
        {
            existingNode.Set($"overflow_{i}", $"val_{i}");
        }

        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var newClient = await TestHelpers.CreateClientAsync<TSerializer>(_cluster, _testOptions, _fakeTime, [TestScope]);

        try
        {
            var newNode = new CacheConsumerNode(newClient.Services.GetRequiredKeyedService<IReplicatedMemoryCache>(TestScope));

            // We wait for the new node to pull the initial stream entirely via the encoder (the slow-path).
            await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

            for (int i = 0; i < ItemCount; i++)
            {
                Assert.Equal($"val_{i}", newNode.Get($"overflow_{i}"));
            }
        }
        finally
        {
            await newClient.StopAsync();
            newClient.Dispose();
        }
    }
}

public abstract class ReplicationTests_WithSketch<TSerializer> : ReplicationTestsBase<TSerializer>
    where TSerializer : class, ICacheEntrySerializer
{
    protected ReplicationTests_WithSketch() : base(new ReplicatedMemoryCacheOptions
    {
        MaterializedSketchSize = 256, // Small enough to easily overflow in the tests, but large enough to test batching.
        ReconciliationPeriod = TimeSpan.FromMilliseconds(100),
        ExpiredMutationGracePeriod = TimeSpan.FromSeconds(1),
        MemorySweepingPeriod = TimeSpan.FromSeconds(2)
    }) { }

    [Fact]
    public async Task SketchOverflow_TransitionsGracefullyToEncoder()
    {
        Assert.Equal(256, _testOptions.MaterializedSketchSize);

        int itemCount = _testOptions.MaterializedSketchSize * 3; // 3x so we blow past the sketch size.
        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 1, TestScope);
        var existingNode = nodes[0];

        for (int i = 0; i < itemCount; i++)
        {
            existingNode.Set($"overflow_{i}", $"val_{i}");
        }

        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var newClient = await TestHelpers.CreateClientAsync<TSerializer>(_cluster, _testOptions, _fakeTime, [TestScope]);

        try
        {
            var newNode = new CacheConsumerNode(newClient.Services.GetRequiredKeyedService<IReplicatedMemoryCache>(TestScope));

            // Under the hood, this will pull the sketch (the fast-path), realize it needs more, 
            // yield while the background hydration task works, and then resume pulling from the encoder.

            await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

            for (int i = 0; i < itemCount; i++)
            {
                Assert.Equal($"val_{i}", newNode.Get($"overflow_{i}"));
            }
        }
        finally
        {
            await newClient.StopAsync();
            newClient.Dispose();
        }
    }
}

public abstract class ReplicationTestsBase<TSerializer>(ReplicatedMemoryCacheOptions options) : IAsyncLifetime
    where TSerializer : class, ICacheEntrySerializer
{
    protected TestCluster _cluster = null!;
    protected readonly List<IHost> _clients = [];
    protected readonly string _testId = Guid.NewGuid().ToString("N");

    protected const string TestScope = "default";

    // These must be instance variables to avoid parallel xUnit test interference.
    protected readonly FakeTimeProvider _fakeTime = new();
    protected readonly ReplicatedMemoryCacheOptions _testOptions = options;

    public async Task InitializeAsync()
    {
        // We use a full TestCluster rather than InProcessTestCluster to ensure we are testing 
        // independent DI containers, actual silo placement, and real gossip propagation.
        // We also spin up two external clients to simulate a realistic heterogeneous deployment (3 silos, 2 clients).
        // We also force the cluster to use TCP sockets instead of the default in-memory transport, otherwise the
        // external clients would be unable to connect to the silo gateways, and we would fail to exercise the
        // real network and serialization boundaries.

        TestStateRegistry.Register(_testId, _fakeTime, _testOptions);

        var builder = new TestClusterBuilder(initialSilosCount: 3);

        builder.Properties["TestId"] = _testId;
        builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;

        _cluster = builder.AddSiloBuilderConfigurator<TestSiloConfigurator>().Build();

        await _cluster.DeployAsync();

        var client1Task = TestHelpers.CreateClientAsync<TSerializer>(_cluster, _testOptions, _fakeTime, [TestScope]);
        var client2Task = TestHelpers.CreateClientAsync<TSerializer>(_cluster, _testOptions, _fakeTime, [TestScope]);

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

                TestHelpers.ConfigureCacheReplication<TSerializer>(services, state.Options, [TestScope]);
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
    public async Task SetOperations_PropagateToOtherNodes()
    {
        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 2, TestScope);
        var nodeA = nodes[0];
        var nodeB = nodes[1];

        var key = "key";
        var val = "val";

        nodeA.Set(key, val);
        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var actualVal = nodeB.Get(key);
        Assert.Equal(val, actualVal);
    }

    [Fact]
    public async Task RemovalOperations_PropagateAsTombstones()
    {
        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 2, TestScope);
        var nodeA = nodes[0];
        var nodeB = nodes[1];

        var key = "key";
        var val = "val";

        nodeA.Set(key, val);
        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var actualVal = nodeB.Get(key);
        Assert.Equal(val, actualVal);

        nodeA.Remove(key);
        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var removedVal = nodeB.Get(key);
        Assert.Null(removedVal);
    }

    [Fact]
    public async Task Expiration_IsRespectedAcrossNodes()
    {
        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 2, TestScope);
        var nodeA = nodes[0];
        var nodeB = nodes[1];

        var key = "key3";
        var val = "val3";
        var ttl = TimeSpan.FromSeconds(3);

        nodeA.Set(key, val, ttl);
        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var beforeExpiration = nodeB.Get(key);
        Assert.Equal(val, beforeExpiration);

        _fakeTime.Advance(ttl);

        var afterExpiration = nodeB.Get(key);
        Assert.Null(afterExpiration); // Logical eviction should hide it on Node B, without replication having been performed.
    }

    [Fact]
    public async Task UpdatesPropagate_And_OlderValuesGetOverwritten()
    {
        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 2, TestScope);
        var nodeA = nodes[0];
        var nodeB = nodes[1];
        var key = "key";

        nodeA.Set(key, "val1");
        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var val1 = nodeB.Get(key);
        Assert.Equal("val1", val1);

        nodeB.Set(key, "val2");
        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var val2 = nodeA.Get(key);
        Assert.Equal("val2", val2); // Node A should have gotten the newer version.
    }

    [Fact]
    public async Task BulkInserts_WorkAcrossStreamBatching()
    {
        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 2, TestScope);
        var nodeA = nodes[0];
        var nodeB = nodes[1];

        // The default SymbolStreamBatchSize is 256! We insert 1000 items to
        // guarantee the stream yields across multiple batches during reconciliation.

        const int ItemCount = 1000;

        for (int i = 0; i < ItemCount; i++)
        {
            nodeA.Set($"key{i}", $"val{i}");
        }

        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        // We randomly sample the beginning, middle, and end of the bulk insert.
        // This gives us good confidence that the entire batch was successfully
        // replicated across the stream, and not just the first batch.

        var firstVal = nodeB.Get("key0");
        var middleVal = nodeB.Get($"key{ItemCount / 2}");
        var lastVal = nodeB.Get($"key{ItemCount - 1}");

        Assert.Equal("val0", firstVal);
        Assert.Equal($"val{ItemCount / 2}", middleVal);
        Assert.Equal($"val{ItemCount - 1}", lastVal);
    }

    [Fact]
    public async Task Chaotic_MultiNode_Convergence()
    {
        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 3, TestScope);
        var nodeA = nodes[0];
        var nodeB = nodes[1];
        var nodeC = nodes[2];

        // We do large parallel inserts: all 3 nodes generate 100 unique keys each at the exact same time.
        for (int i = 0; i < 100; i++)
        {
            nodeA.Set($"key_a_{i}", $"val_a_{i}");
            nodeB.Set($"key_b_{i}", $"val_b_{i}");
            nodeC.Set($"key_c_{i}", $"val_c_{i}");
        }

        _fakeTime.Advance(TimeSpan.FromSeconds(1)); // To ensure distinct clock versions for the next phases!

        // Node A, B, and C all try to own the same key sequentially,
        // we verify that the last writer (Node C) wins (LWW) across the cluster.
        var conflictKey = "conflict_key";

        nodeA.Set(conflictKey, "winner_A");
        _fakeTime.Advance(TimeSpan.FromMilliseconds(10));

        nodeB.Set(conflictKey, "winner_B");
        _fakeTime.Advance(TimeSpan.FromMilliseconds(10));

        nodeC.Set(conflictKey, "winner_C");

        // Node B sets a value, but 10ms later Node A deletes it.
        var tombstoneKey = "tombstone_key";

        nodeB.Set(tombstoneKey, "i_am_alive");
        _fakeTime.Advance(TimeSpan.FromMilliseconds(10));

        nodeA.Remove(tombstoneKey); // Node A issues the chronological kill order.

        // We wait for epidemic gossip to heal the cluster!
        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        // Check LWW Overwrites: The entire cluster MUST agree that Node C won.
        Assert.Equal("winner_C", nodeA.Get(conflictKey));
        Assert.Equal("winner_C", nodeB.Get(conflictKey));
        Assert.Equal("winner_C", nodeC.Get(conflictKey));

        // Check Tombstone Resolution: The key must be null everywhere.
        Assert.Null(nodeA.Get(tombstoneKey));
        Assert.Null(nodeB.Get(tombstoneKey));
        Assert.Null(nodeC.Get(tombstoneKey));

        // We randomly sample keys across boundaries.

        // Node C should have keys originated by Node A
        Assert.Equal("val_a_42", nodeC.Get("key_a_42"));
        Assert.Equal("val_a_99", nodeC.Get("key_a_99"));

        // Node A should have keys originated by Node B
        Assert.Equal("val_b_7", nodeA.Get("key_b_7"));
        Assert.Equal("val_b_88", nodeA.Get("key_b_88"));

        // Node B should have keys originated by Node C
        Assert.Equal("val_c_0", nodeB.Get("key_c_0"));
        Assert.Equal("val_c_55", nodeB.Get("key_c_55"));
    }

    [Fact]
    public async Task LateJoiner_CatchesUpFully_AfterMissinManyMutations()
    {
        const int ItemCount = 500;

        var nodes = TestHelpers.GetRandomNodes(_cluster, _clients, 1, TestScope);
        var existingNode = nodes[0];

        for (int i = 0; i < ItemCount; i++)
        {
            existingNode.Set($"key_{i}", $"val_{i}");
        }

        // We allow the existing cluster to completely stabilize and replicate.
        await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

        var newClient = await TestHelpers.CreateClientAsync<TSerializer>(_cluster, _testOptions, _fakeTime, [TestScope]);

        try
        {
            var newNode = new CacheConsumerNode(newClient.Services.GetRequiredKeyedService<IReplicatedMemoryCache>(TestScope));
            var initialCheck = newNode.Get("key_0");
            Assert.Null(initialCheck);

            // The new client's ReplicationService will pick a random node to sync with, we allow 
            // the R-IBLT to detect and apply the massive entries delta.
            await TestHelpers.WaitForGossipPropagationAsync(_fakeTime, _testOptions.ReconciliationPeriod);

            for (int i = 0; i < ItemCount; i++)
            {
                var val = newNode.Get($"key_{i}");
                Assert.Equal($"val_{i}", val);
            }
        }
        finally
        {
            await newClient.StopAsync();
            newClient.Dispose();
        }
    }
}