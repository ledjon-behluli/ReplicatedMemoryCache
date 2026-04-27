using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using System.Runtime.CompilerServices;

namespace Ledjon.ReplicatedMemoryCache;

[Alias("Ledjon.ReplicatedMemoryCache.IReplicationRegistry")]
internal interface IReplicationRegistry : IGrainWithStringKey
{
    [Alias("RegisterOrUpdate")] Task RegisterOrUpdate(IReplicationNode node);
    [Alias("Unregister")] Task Unregister(IReplicationNode node);
    [Alias("PickRandom")] ValueTask<IReplicationNode?> PickRandom(IReplicationNode requester);
}

[KeepAlive, Reentrant]
internal partial class ReplicationRegistry(
    TimeProvider timeProvider, IOptions<ReplicatedMemoryCacheOptions> options, ILogger<ReplicationRegistry> logger)
        : Grain, IReplicationRegistry, IGrainMigrationParticipant
{
    [GenerateSerializer]
    [Alias("Ledjon.ReplicatedMemoryCache.NodeState")]
    public class NodeState
    {
        [Id(0)] public DateTime LastSeen { get; set; }
        [Id(1)] public int MissedHeartbeats { get; set; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSuspected(int threshold) => MissedHeartbeats > threshold;
    }

    private const string StateKey = "rmc_replication_nodes";

    private Dictionary<IReplicationNode, NodeState> _nodes = [];

    private readonly List<IReplicationNode> _toEvict = [];
    private readonly List<IReplicationNode> _toProbe = [];
    private readonly List<IReplicationNode> _healthyNodes = [];
    private readonly List<Task> _probeTasks = [];

    private readonly TimeSpan _livenessPeriod = options.Value.NodeLivenessPeriod;
    private readonly TimeSpan _failureDetectionPeriod = options.Value.NodeLivenessPeriod / 2;
    private readonly TimeSpan _maxAllowedIdlenessPeriod = (2 * options.Value.NodeLivenessPeriod) + (options.Value.NodeLivenessPeriod / 2 * options.Value.MissedHeartbeatsBeforeIndirectProbing);
    private readonly int _missedHeartbeatsBeforeIndirectProbing = options.Value.MissedHeartbeatsBeforeIndirectProbing;

    public void OnDehydrate(IDehydrationContext context) => context.TryAddValue(StateKey, _nodes);

    public void OnRehydrate(IRehydrationContext context)
    {
        if (context.TryGetValue<Dictionary<IReplicationNode, NodeState>>(StateKey, out var state))
        {
            _nodes = new(state);
        }
    }

    public override Task OnActivateAsync(CancellationToken ct)
    {
        this.RegisterGrainTimer(FailureDetectionLoop, _failureDetectionPeriod, _failureDetectionPeriod);
        return Task.CompletedTask;
    }

    public Task RegisterOrUpdate(IReplicationNode node)
    {
        var utcNow = timeProvider.GetUtcNow().DateTime;

        if (_nodes.TryGetValue(node, out var entry))
        {
            entry.LastSeen = utcNow;
            entry.MissedHeartbeats = 0;
        }
        else
        {
            _nodes[node] = new() { LastSeen = utcNow };
            LogNodeRegistered(logger, node);
        }

        return Task.CompletedTask;
    }

    public Task Unregister(IReplicationNode node)
    {
        _nodes.Remove(node);
        return Task.CompletedTask;
    }

    public ValueTask<IReplicationNode?> PickRandom(IReplicationNode requester)
    {
        IReplicationNode? target = null;

        int count = 0;  // Number of eligible nodes encountered so far.

        foreach (var (node, nodeState) in _nodes)
        {
            if (node.Equals(requester) || nodeState.IsSuspected(_missedHeartbeatsBeforeIndirectProbing))
            {
                continue; // We skip the requester itself, and any nodes which are suspected.
            }

            count++; // We have found an eligible node, so increment the count.

            // We use reservoir sampling to select a random node from the eligible nodes.
            // The probability of selecting the current node is 1/count, ensuring uniform randomness.

            if (Random.Shared.Next(count) == 0)
            {
                target = node;
            }
        }

        return ValueTask.FromResult(target);
    }

    private async Task FailureDetectionLoop()
    {
        var utcNow = timeProvider.GetUtcNow().DateTime;

        _toEvict.Clear();
        _toProbe.Clear();
        _probeTasks.Clear();
        _healthyNodes.Clear();

        foreach (var (node, nodeState) in _nodes)
        {
            var idleTime = utcNow - nodeState.LastSeen;
            if (idleTime > _maxAllowedIdlenessPeriod)
            {
                _toEvict.Add(node);
                continue;
            }

            if (idleTime > _livenessPeriod)
            {
                nodeState.MissedHeartbeats++;

                if (nodeState.IsSuspected(_missedHeartbeatsBeforeIndirectProbing))
                {
                    if (nodeState.MissedHeartbeats == _missedHeartbeatsBeforeIndirectProbing + 1)
                    {
                        // We trigger probing and log on the *excat* tick the threshold is crossed, not every time!
                        _toProbe.Add(node);
                        LogNodeSuspected(logger, node);
                    }

                    continue;
                }
            }

            _healthyNodes.Add(node);
        }

        foreach (var node in _toEvict)
        {
            _nodes.Remove(node);
            LogNodeEvicted(logger, node);
        }

        if (_healthyNodes.Count == 0 || _toProbe.Count == 0)
        {
            return;
        }

        foreach (var suspect in _toProbe)
        {
            var prober = _healthyNodes[Random.Shared.Next(_healthyNodes.Count)];
            _probeTasks.Add(ExecuteIndirectProbing(prober, suspect));
        }

        await Task.WhenAll(_probeTasks);
    }

    private async Task ExecuteIndirectProbing(IReplicationNode prober, IReplicationNode suspect)
    {
        try
        {
            var isAlive = await prober.Probe(suspect);
            if (isAlive)
            {
                if (_nodes.TryGetValue(suspect, out var state))
                {
                    state.MissedHeartbeats = 0;
                    state.LastSeen = timeProvider.GetUtcNow().DateTime;
                }

                LogIndirectProbeSuccess(logger, prober, suspect);
            }
            else
            {
                _nodes.Remove(suspect);
                LogIndirectProbeFailed(logger, prober, suspect);
            }
        }
        catch (Exception ex)
        {
            LogProberFailed(logger, ex, prober, suspect);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Node} registered with the registry.")]
    private static partial void LogNodeRegistered(ILogger logger, IReplicationNode node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Node} missed its heartbeat and is now suspected. Initiating indirect probe now.")]
    private static partial void LogNodeSuspected(ILogger logger, IReplicationNode node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Node} evicted from registry due to idleness.")]
    private static partial void LogNodeEvicted(ILogger logger, IReplicationNode node);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Prober} confirmed suspected node {Suspecter} is alive. Suspicion is now cleared.")]
    private static partial void LogIndirectProbeSuccess(ILogger logger, IReplicationNode prober, IReplicationNode suspecter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Prober} failed to reach {Suspecter}. Node has been evicted.")]
    private static partial void LogIndirectProbeFailed(ILogger logger, IReplicationNode prober, IReplicationNode suspecter);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Prober} failed to execute indirect probe on {Suspecter}.")]
    private static partial void LogProberFailed(ILogger logger, Exception exception, IReplicationNode prober, IReplicationNode suspecter);
}