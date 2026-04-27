using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Orleans.Concurrency;
using Orleans.TestingHost;

namespace Ledjon.ReplicatedMemoryCache.Tests;

[Collection(SequentialTestsCollection.Name)]
public class RegistryTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    private readonly string _testId = Guid.NewGuid().ToString("N");
    private readonly FakeTimeProvider _fakeTime = new();
    private readonly ReplicatedMemoryCacheOptions _testOptions = new()
    {
        MissedHeartbeatsBeforeIndirectProbing = 1, // Its already 1 by default, but just so its apparent!
        NodeLivenessPeriod = TimeSpan.FromSeconds(10)
    };

    public async Task InitializeAsync()
    {
        TestStateRegistry.Register(_testId, _fakeTime, _testOptions);

        var builder = new TestClusterBuilder(initialSilosCount: 1);

        builder.Properties["TestId"] = _testId;
        builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;

        _cluster = builder.AddSiloBuilderConfigurator<TestSiloConfigurator>().Build();

        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await(_cluster?.StopAllSilosAsync() ?? Task.CompletedTask);
        TestStateRegistry.Remove(_testId);
    }

    private class TestSiloConfigurator : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices((context, services) =>
            {
                var state = TestStateRegistry.Get(context.Configuration["TestId"]!);

                services.AddSingleton<TimeProvider>(state.Time);
                services.Configure<ReplicatedMemoryCacheOptions>(options =>
                {
                    options.NodeLivenessPeriod = state.Options.NodeLivenessPeriod;
                    options.MissedHeartbeatsBeforeIndirectProbing = state.Options.MissedHeartbeatsBeforeIndirectProbing;
                });
            });
        }
    }

    private IReplicationNode GetNodeReference(TestNode node) => _cluster.Client.CreateObjectReference<IReplicationNode>(node);
    private IReplicationRegistry GetRandomRegistryGrain() => _cluster.Client.GetGrain<IReplicationRegistry>(Guid.NewGuid().ToString());

    [Fact]
    public async Task NodeRegistersSuccessfully_AndCanBePicked()
    {
        var registry = GetRandomRegistryGrain();

        var nodeA = new TestNode();
        var nodeB = new TestNode();

        var refA = GetNodeReference(nodeA);
        var refB = GetNodeReference(nodeB);

        await registry.RegisterOrUpdate(refA);
        await registry.RegisterOrUpdate(refB);

        var picked = await registry.PickRandom(refA);

        Assert.Equal(refB, picked);
    }

    [Fact]
    public async Task PickRandom_ReturnsNull_IfNoOtherActiveNodesExist()
    {
        var registry = GetRandomRegistryGrain();

        var nodeA = new TestNode();
        var refA = GetNodeReference(nodeA);

        await registry.RegisterOrUpdate(refA);

        var picked = await registry.PickRandom(refA);

        Assert.Null(picked);
    }

    [Fact]
    public async Task UnregisteredNode_CanStillPickPeer()
    {
        var registry = GetRandomRegistryGrain();

        var nodeA = new TestNode();
        var refA = GetNodeReference(nodeA);

        await registry.RegisterOrUpdate(refA);

        var nodeB = new TestNode();
        var refB = GetNodeReference(nodeB);

        // We didnt register node B, but it should still be able to pick node A.
        // If a node is temporarily evicted from the registry due to a missed heartbeat,
        // or a brief network stutter, it shouldn't completely halt its cache synchronization.

        var picked = await registry.PickRandom(refB);
        Assert.Equal(refA, picked);
    }

    [Fact]
    public async Task MissedHeartbeatSuspicion_PreventsNodeFromBeingPicked()
    {
        var registry = GetRandomRegistryGrain();

        var nodeA = new TestNode();
        var nodeB = new TestNode();

        var refA = GetNodeReference(nodeA);
        var refB = GetNodeReference(nodeB);

        await registry.RegisterOrUpdate(refA);
        await registry.RegisterOrUpdate(refB);

        // We must stagger the time advancement so the grain timer doesnt consolidate all ticks into one.

        _fakeTime.Advance(TimeSpan.FromSeconds(6)); // Tick 1 (5s)
        _fakeTime.Advance(TimeSpan.FromSeconds(6)); // Tick 2 (10s)
        _fakeTime.Advance(TimeSpan.FromSeconds(6)); // Tick 3 (15s) -> MissedHeartbeats = 1
        _fakeTime.Advance(TimeSpan.FromSeconds(6)); // Tick 4 (20s) -> MissedHeartbeats = 2 (threshold crossed)

        // Node A asks for a peer. Node B crossed the threshold and is suspected, so it is filtered out.
        var picked = await registry.PickRandom(refA);
        Assert.Null(picked);
    }

    [Fact]
    public async Task SuspectedNode_IsSaved_BySuccessfulIndirectProbe()
    {
        var registry = GetRandomRegistryGrain();

        var healthyProber = new TestNode { IAmAlive = true };
        var suspectedNode = new TestNode { IAmAlive = true };

        var refProber = GetNodeReference(healthyProber);
        var refSuspect = GetNodeReference(suspectedNode);

        await registry.RegisterOrUpdate(refProber);
        await registry.RegisterOrUpdate(refSuspect);

        // We advance by 6 seconds -> Timer runs -> Neither is > 10s idle.
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        
        // Prober pings to stay healthy. (Suspect = 6s old, Prober = 0s old)
        await registry.RegisterOrUpdate(refProber);

        // Next we advance by another 6 seconds -> Timer runs ->
        // Suspect is 12s old. It misses its FIRST heartbeat (MissedHeartbeats = 1). 
        // Because 1 is not > 1, no probe fires yet!
        _fakeTime.Advance(TimeSpan.FromSeconds(6));

        // Prober pings to stay healthy again. (Suspect = 12s old, Prober = 0s old)
        await registry.RegisterOrUpdate(refProber);

        // We advance for another 6 seconds -> Timer runs ->
        // Suspect is 18s old. It misses its second heartbeat (MissedHeartbeats = 2).
        // Because 2 > 1, the threshold is crossed, the probe fires!
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        
        await healthyProber.ProbeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // We force the random picker to only have refSuspect as an option by unregistering the prober.
        await registry.Unregister(refProber);

        var nodeC = new TestNode();
        var refC = GetNodeReference(nodeC);

        // Node C asks for a random peer. It should be able to get the suspect.
        var picked = await registry.PickRandom(refC);
        Assert.Equal(refSuspect, picked);
    }

    [Fact]
    public async Task UnresponsiveNode_IsEvicted_ByFailedIndirectProbe()
    {
        var registry = GetRandomRegistryGrain();

        var healthyProber = new TestNode { IAmAlive = true };
        var deadNode = new TestNode { IAmAlive = false }; // This will cause Probe to return false

        var refProber = GetNodeReference(healthyProber);
        var refDead = GetNodeReference(deadNode);

        await registry.RegisterOrUpdate(refProber);
        await registry.RegisterOrUpdate(refDead);

        // We use the exact same staggered timeline as above
        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await registry.RegisterOrUpdate(refProber);

        _fakeTime.Advance(TimeSpan.FromSeconds(6));
        await registry.RegisterOrUpdate(refProber);

        _fakeTime.Advance(TimeSpan.FromSeconds(6));

        await healthyProber.ProbeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var nodeC = new TestNode();
        var refC = GetNodeReference(nodeC);
        await registry.RegisterOrUpdate(refC);

        // We hide the prober so we only test against dead node!
        await registry.Unregister(refProber);

        // The suspect node should be completely removed from the registry now.
        var picked = await registry.PickRandom(refC);
        Assert.Null(picked);
    }

    private class TestNode : IReplicationNode
    {
        public bool IAmAlive { get; set; } = true;

        public TaskCompletionSource ProbeCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ping()
        {
            if (!IAmAlive)
            {
                throw new Exception("Simulated network failure");
            }

            return Task.CompletedTask;
        }

        public async Task<bool> Probe(IReplicationNode target)
        {
            if (!IAmAlive)
            {
                throw new Exception("Prober is dead");
            }

            bool isAlive;

            try
            {
                await target.Ping();
                isAlive = true;
            }
            catch
            {
                isAlive = false;
            }

            Task.Run(async () =>
            {
                // We schedule the TCS completion slightly in the future so that the registry grain has enough time
                // to process this return value before the test thread resumes and runs assertions.

                await Task.Delay(10);
                ProbeCompleted.TrySetResult();
            }).Ignore();

            return isAlive;
        }

        public ValueTask<Guid> StartSnapshotStream() => ValueTask.FromResult(Guid.NewGuid());
        public ValueTask<Immutable<List<CacheMutation>>> PullSnapshotBatch(Guid streamId, int batchSize) => ValueTask.FromResult(new Immutable<List<CacheMutation>>([]));
        public ValueTask CloseSnapshotStream(Guid streamId) => ValueTask.CompletedTask;

        public ValueTask<Guid> StartSymbolStream() => ValueTask.FromResult(Guid.NewGuid());
        public ValueTask<Immutable<List<CodedSymbol>>> PullSymbolBatch(Guid streamId, int batch) => ValueTask.FromResult(new Immutable<List<CodedSymbol>>([]));
        public ValueTask CloseSymbolStream(Guid streamId) => ValueTask.CompletedTask;

        public ValueTask<Immutable<List<CacheMutation>>> GetMutations(List<Guid> mutationIds) => ValueTask.FromResult(new Immutable<List<CacheMutation>>([]));
        public ValueTask ApplyMutations(List<CacheMutation> mutations) => ValueTask.CompletedTask;
    }
}