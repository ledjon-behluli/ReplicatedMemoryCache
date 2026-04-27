using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers;

namespace Ledjon.ReplicatedMemoryCache;

// If we have a global "clock" which picks a node as the "leader" and that node
// starts to generate coded symbols and begins streaming to all other nodes,
// each node would stop when the set diff is zero between the starting node and itself,
// this way we could achive reconcilliation in one round.

// We could also have the leader node maintain a universal sequence of coded symbols,
// and streams it to anyone who wishes to reconcile. R-IBLT also allows that node to
// incrementally update the coded symbols as it modifies the set, further amortizing the encoding costs.

// This complicates matters though, because if the leader crashes, the cluster halts until a new one is elected.
// With epidemic gossip any node can die, any node can join, and the math guarantees eventual consistency
// without a single leader bottlenecking the process.

internal partial class ReplicationService(
    IClusterClient client, IServiceLifecycle lifecycle, IServiceProvider proivder,
    IEnumerable<NamedReplicationScope> scopes, ILogger<ReplicationService> logger)
        : BackgroundService
{
    // Need to keep a strong refs to the observer implementions, otherwise GC will wipe them!
    private readonly List<ReplicationNode> _localNodes = [];

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await lifecycle.Started.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Task.WhenAll(scopes.Select(scope => StartReplicationAsync(scope.Value, cancellationToken)));
    }

    private async Task StartReplicationAsync(string scopeName, CancellationToken cancellationToken)
    {
        var localCache = proivder.GetRequiredKeyedService<LocalNodeCache>(scopeName);
        var localNode = new ReplicationNode(localCache);

        lock (_localNodes) 
        {
            _localNodes.Add(localNode);
        }

        var registry = client.GetGrain<IReplicationRegistry>(scopeName);
        var localNodeRef = client.CreateObjectReference<IReplicationNode>(localNode);

        lifecycle.Stopping.Register(async (_, _) =>
        {
            LogReplicationServiceStopping(logger, localNodeRef);
            await registry.Unregister(localNodeRef);
        }, terminateOnError: false);

        var options = proivder.GetRequiredService<IOptionsMonitor<ReplicatedMemoryCacheOptions>>().Get(scopeName);

        await registry.RegisterOrUpdate(localNodeRef); // We are ready to participate in gossip now!

        LogReplicationServiceStarted(logger, localNode);

        using var sweepTimer = new PeriodicTimer(options.MemorySweepingPeriod);
        using var heartbeatTimer = new PeriodicTimer(options.NodeLivenessPeriod);
        using var reconciliationTimer = new PeriodicTimer(options.ReconciliationPeriod);

        var sweepTimerTask = RunTimerAsync(sweepTimer, _ =>
        {
            try
            {
                localCache.SweepExpiredMutations();
            }
            catch (Exception ex)
            {
                LogMutationSweepFailed(logger, ex);
            }

            return Task.CompletedTask;
        }, cancellationToken);

        var heartbeatTimerTask = RunTimerAsync(heartbeatTimer, async _ =>
        {
            try
            {
                await registry.RegisterOrUpdate(localNodeRef);
            }
            catch (Exception ex)
            {
                LogRegistryHeartbeatFailed(logger, ex, localNodeRef);
            }
        }, cancellationToken);

        var reconcileTimerTask = RunTimerAsync(reconciliationTimer, async token =>
        {
            IReplicationNode? remoteNodeRef = null;

            try
            {
                remoteNodeRef = await registry.PickRandom(localNodeRef);
            }
            catch (Exception ex)
            {
                LogReplicationPeerSelectionFailed(logger, ex);
            }

            if (remoteNodeRef is not null)
            {
                try
                {
                    await ReconcileAsync(localCache, remoteNodeRef, options, token);
                }
                catch (Exception ex)
                {
                    LogReconciliationFailed(logger, ex, localNodeRef, remoteNodeRef);
                }
            }
        }, cancellationToken);

        await Task.WhenAll(heartbeatTimerTask, sweepTimerTask, reconcileTimerTask);
    }

    private async Task RunTimerAsync(PeriodicTimer timer, Func<CancellationToken, Task> callback, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await callback(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogTimerCallbackFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore, we are shutting down!
        }
    }

    private async Task ReconcileAsync(
        LocalNodeCache localCache, IReplicationNode remoteNode,
        ReplicatedMemoryCacheOptions options, CancellationToken cancellationToken)
    {
        LogReconciliationStarted(logger, remoteNode);

        var streamId = await remoteNode.StartSymbolStream();

        LogSymbolStreamOpened(logger, streamId, remoteNode);

        var decoder = new RibltDecoder();
        var pulledSymbols = 0;
        var maxSymbolsToPull = options.MaxSymbolsToPull;
        var symbolStreamBatchSize = options.SymbolStreamBatchSize;
        var isBaselineLoaded = false;
        var sketch = localCache.Sketch;
        var isFastPathActive = sketch is { Size: > 0 };
        var sliceBuffer = isFastPathActive ? ArrayPool<CodedSymbol>.Shared.Rent(symbolStreamBatchSize) : null;

        // We cap the retry delay between 10ms and 100ms so we dont stall the stream waiting 
        // for the remote task, nor do we tight-loop and accidentally ddos the remote node.
        var retryDelayMs = (int)Math.Clamp(options.ReconciliationPeriod.TotalMilliseconds / 10, 10, 100);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var currentBatchSize = Math.Min(symbolStreamBatchSize, maxSymbolsToPull - pulledSymbols);
                if (currentBatchSize <= 0)
                {
                    break;
                }

                var remoteBatch = (await remoteNode.PullSymbolBatch(streamId, currentBatchSize)).Value;
                if (remoteBatch.Count == 0)
                {
                    // The remote node is currently building its encoder in the background 
                    // and returned an empty batch to avoid blocking its observer queue.
                    // So we back off for a short time and retry pulling the exact same batch.

                    await Task.Delay(retryDelayMs, cancellationToken);
                    
                    continue;
                }

                var sliceLength = isFastPathActive ? sketch!.ReadSlice(pulledSymbols, sliceBuffer!.AsSpan(0, remoteBatch.Count)) : 0;

                for (int i = 0; i < remoteBatch.Count; i++)
                {
                    var remoteSymbol = remoteBatch[i];

                    if (isFastPathActive && i < sliceLength)
                    {
                        // Fast-Path: We do in-place subtraction using the materialized sketch in O(1) time.
                        // We neutralize the shared state immediately without dictionary lookups.
                        // The decoder therefore already sees the difference for every symbol covered by the sketch.
                        // We dont call decoder.AddMutation for those mutations, because the sketch has already contributed their effect.

                        remoteSymbol.Apply(sliceBuffer![i], SymbolOperation.Remove);

                        // Since we removed the current symbol of the slice to the remote symbol,
                        // the remote symbol itself becomes the "difference" symbol, so we apply it to the decoder.

                        decoder.ApplyDifferenceSymbol(remoteSymbol);

                    }
                    else
                    {
                        // Slow-Path: We have exhausted the sketch's max capacity (or there is no sketch at all),
                        // so we need to fallback to applying rach remote symbol to the decoder so we can neutralize
                        // the current remote symbol for our "view".

                        if (isFastPathActive)
                        {
                            isFastPathActive = false; // So we *dont* re-apply the mutations to the decoder the next round!
                        }

                        if (!isBaselineLoaded)
                        {
                            // The sketch has only represented the prefix of the rateless stream.
                            // Until now the decoder's baseline (_current) window has intentionally remained empty,
                            // because each incoming symbol already had the local contribution removed.
                            // From this point onward we are receiving raw remote symbols, so we must materialize the
                            // decoder's baseline window exactly once, just as it would have happened if no sketch existed to begin with.

                            isBaselineLoaded = true;

                            var baselineOffset = (long)pulledSymbols;

                            localCache.ForEachMutation((decoder, baselineOffset), 
                                (state, mutation) => state.decoder.AddMutationAtOffset(mutation, state.baselineOffset));
                        }

                        decoder.ApplySymbol(remoteSymbol);
                    }

                    pulledSymbols++;
                }

                LogSymbolBatchPulled(logger, remoteBatch.Count, remoteNode, pulledSymbols);

                decoder.TryDecode();

                if (decoder.IsCompleted || pulledSymbols >= maxSymbolsToPull)
                {
                    break;
                }
            }
        }
        finally
        {
            if (sliceBuffer is { } buffer)
            {
                ArrayPool<CodedSymbol>.Shared.Return(buffer);
            }

            await remoteNode.CloseSymbolStream(streamId);
            
            LogSymbolStreamClosed(logger, streamId, remoteNode);
        }

        if (!decoder.IsCompleted)
        {
            LogReconciliationDecodeFailed(logger, remoteNode, pulledSymbols);
            return;
        }

        var mutationTransferBatchSize = options.MutationTransferBatchSize;

        if (decoder.RemoteSymbols is { Count: > 0 } missingLocally)
        {
            LogPullingMissingMutations(logger, missingLocally.Count, remoteNode);

            var buffer = new List<Guid>(Math.Min(missingLocally.Count, mutationTransferBatchSize));

            foreach (var hashedSymbol in missingLocally)
            {
                buffer.Add(hashedSymbol.Symbol.Id);

                if (buffer.Count == mutationTransferBatchSize)
                {
                    var mutations = await remoteNode.GetMutations(buffer);

                    foreach (var mutation in mutations.Value)
                    {
                        localCache.ApplyMutation(mutation);
                    }

                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                var mutations = await remoteNode.GetMutations(buffer);

                foreach (var mutation in mutations.Value)
                {
                    localCache.ApplyMutation(mutation);
                }
            }
        }

        if (decoder.LocalSymbols is { Count: > 0 } missingRemotely)
        {
            LogPushingMissingMutations(logger, missingRemotely.Count, remoteNode);

            var buffer = new List<CacheMutation>(Math.Min(missingRemotely.Count, mutationTransferBatchSize));

            foreach (var hashedSymbol in missingRemotely)
            {
                var mutation = localCache.GetMutation(hashedSymbol.Symbol.Id);
                if (mutation is null)
                {
                    continue;
                }

                buffer.Add(mutation);

                if (buffer.Count == mutationTransferBatchSize)
                {
                    await remoteNode.ApplyMutations(buffer);
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                await remoteNode.ApplyMutations(buffer);
                buffer.Clear();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{LocalNode} has started.")]
    private static partial void LogReplicationServiceStarted(ILogger logger, IReplicationNode localNode);

    [LoggerMessage(Level = LogLevel.Information, Message = "{LocalNode} is leaving the cluster. Will stop soon!")]
    private static partial void LogReplicationServiceStopping(ILogger logger, IReplicationNode localNode);

    [LoggerMessage(Level = LogLevel.Error, Message = "{LocalNode} failed to send heartbeat to registry.")]
    private static partial void LogRegistryHeartbeatFailed(ILogger logger, Exception exception, IReplicationNode localNode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to sweep expired mutations.")]
    private static partial void LogMutationSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to pick replication peer from registry.")]
    private static partial void LogReplicationPeerSelectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache reconciliation failed between {LocalNode} and {RemoteNode}.")]
    private static partial void LogReconciliationFailed(ILogger logger, Exception exception, IReplicationNode localNode, IReplicationNode remoteNode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Timer callback execution failed.")]
    private static partial void LogTimerCallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Starting reconciliation with remote node {RemoteNode}.")]
    private static partial void LogReconciliationStarted(ILogger logger, IReplicationNode remoteNode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Opened symbol stream {StreamId} with remote node {RemoteNode}.")]
    private static partial void LogSymbolStreamOpened(ILogger logger, Guid streamId, IReplicationNode remoteNode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Pulled {BatchSize} symbols from {RemoteNode}. Total pulled: {PulledSymbols}.")]
    private static partial void LogSymbolBatchPulled(ILogger logger, int batchSize, IReplicationNode remoteNode, int pulledSymbols);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to fully decode reconciliation stream from {RemoteNode} after pulling {PulledSymbols} symbols.")]
    private static partial void LogReconciliationDecodeFailed(ILogger logger, IReplicationNode remoteNode, int pulledSymbols);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Closed symbol stream {StreamId} with remote node {RemoteNode}.")]
    private static partial void LogSymbolStreamClosed(ILogger logger, Guid streamId, IReplicationNode remoteNode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Pulling {MissingCount} missing mutations from {RemoteNode}.")]
    private static partial void LogPullingMissingMutations(ILogger logger, int missingCount, IReplicationNode remoteNode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Pushing {MissingCount} missing mutations to {RemoteNode}.")]
    private static partial void LogPushingMissingMutations(ILogger logger, int missingCount, IReplicationNode remoteNode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Reconciliation with {RemoteNode} completed successfully. Pulled: {PulledCount}, Pushed: {PushedCount}.")]
    private static partial void LogReconciliationCompleted(ILogger logger, IReplicationNode remoteNode, int pulledCount, int pushedCount);
}