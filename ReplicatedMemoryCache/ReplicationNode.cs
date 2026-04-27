using Orleans.Concurrency;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ledjon.ReplicatedMemoryCache;

[Alias("Ledjon.ReplicatedMemoryCache.IReplicationNode")]
internal interface IReplicationNode : IGrainObserver
{
    [Alias("Ping"), AlwaysInterleave] Task Ping();
    [Alias("Probe"), AlwaysInterleave] Task<bool> Probe(IReplicationNode target);

    [Alias("StartSnapshotStream")] ValueTask<Guid> StartSnapshotStream();
    [Alias("PullSnapshotBatch")] ValueTask<Immutable<List<CacheMutation>>> PullSnapshotBatch(Guid streamId, int batchSize);
    [Alias("CloseSnapshotStream")] ValueTask CloseSnapshotStream(Guid streamId);

    [Alias("StartSymbolStream")] ValueTask<Guid> StartSymbolStream();
    [Alias("PullSymbolBatch")] ValueTask<Immutable<List<CodedSymbol>>> PullSymbolBatch(Guid streamId, int batchSize);
    [Alias("CloseSymbolStream")] ValueTask CloseSymbolStream(Guid streamId);

    [Alias("GetMutations"), AlwaysInterleave] ValueTask<Immutable<List<CacheMutation>>> GetMutations([Immutable] List<Guid> mutationIds);
    [Alias("ApplyMutations"), AlwaysInterleave] ValueTask ApplyMutations([Immutable] List<CacheMutation> mutations);
}

internal class ReplicationNode(LocalNodeCache localCache) : IReplicationNode
{
    private class StreamState
    {
        public int Offset { get; set; }
        public RibltEncoder? Encoder { get; set; }
        public Task<RibltEncoder>? HydrationTask { get; set; }
    }

    private class SnapshotState : IDisposable
    {
        public required IEnumerator<CacheMutation> Enumerator { get; init; }
        public void Dispose() => Enumerator.Dispose();
    }

    private readonly Dictionary<Guid, StreamState> _activeStreams = [];
    private readonly Dictionary<Guid, SnapshotState> _activeSnapshots = [];
    private readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

    // We use the replication node as the ping target because we support both silos and external 
    // clients, requiring us to rely on observers for node-to-node targeting.
    // As of Orleans 10.0.0 (PR 9834), observer methods support [AlwaysInterleave], which handles 
    // concurrent pings. However, to support older Orleans runtimes where  observer interleaving is ignored,
    // we must still defensively prevent thread starvation.

    // To ensure pings do not time out on older runtimes, the heavy-duty CPU work of hydrating the 
    // encoder (when the cache diff exceeds the sketch) is offloaded outside the grain context. 
    // Callers respect this by checking if the returned batch is empty, which acts as a signal 
    // to back off and allows the observer's queue a chance to process pending pings.

    // Probe() can interleave too as it is safe, but also Get/Apply Mutation method too as
    // the delegate to the local node cache which is thread-safe.

    public Task Ping() => Task.CompletedTask;

    public async Task<bool> Probe(IReplicationNode target)
    {
        try
        {
            await target.Ping().WaitAsync(PingTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public ValueTask<Guid> StartSnapshotStream()
    {
        var streamId = Guid.NewGuid();

        _activeSnapshots[streamId] = new SnapshotState
        {
            // We just reference the enumerator so that we dont have to allocated a huge copy of all (local) mutations.
            Enumerator = localCache.Mutations.GetEnumerator()
        };

        return ValueTask.FromResult(streamId);
    }

    public ValueTask<Immutable<List<CacheMutation>>> PullSnapshotBatch(Guid streamId, int batchSize)
    {
        if (!_activeSnapshots.TryGetValue(streamId, out var state))
        {
            throw new InvalidOperationException($"Snapshot session = {streamId} is not active.");
        }

        var batch = new List<CacheMutation>(batchSize);

        while (batch.Count < batchSize && state.Enumerator.MoveNext())
        {
            batch.Add(state.Enumerator.Current);
        }

        return new ValueTask<Immutable<List<CacheMutation>>>(batch.AsImmutable());
    }

    public ValueTask CloseSnapshotStream(Guid streamId)
    {
        if (_activeSnapshots.Remove(streamId, out var state))
        {
            state.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<Guid> StartSymbolStream()
    {
        var streamId = Guid.NewGuid();

        _activeStreams[streamId] = new StreamState { Offset = 0 };

        return ValueTask.FromResult(streamId);
    }

    public async ValueTask<Immutable<List<CodedSymbol>>> PullSymbolBatch(Guid streamId, int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize, nameof(batchSize));

        if (!_activeStreams.TryGetValue(streamId, out var state))
        {
            throw new InvalidOperationException($"Stream session = {streamId} is not active or has expired.");
        }

        var symbols = new List<CodedSymbol>(batchSize);

        // Fast-path: If the requested batch is fully contained within the sketch (and its used ofc),
        // we can serve it directly from the sketch without initializing and running the mutations through the encoder.
        if (localCache.Sketch is { } sketch && state.Offset < sketch.Size)
        {
            var countToRead = Math.Min(batchSize, sketch.Size - state.Offset);

            CollectionsMarshal.SetCount(symbols, countToRead);

            var destSpan = CollectionsMarshal.AsSpan(symbols);
            var readCount = sketch.ReadSlice(state.Offset, destSpan);

            if (readCount < countToRead)
            {
                // Sketch returned less than expected, so we shrink the list back down
                CollectionsMarshal.SetCount(symbols, readCount);
            }

            state.Offset += readCount;
            batchSize -= readCount;

            if (batchSize == 0)
            {
                // Optimization: If we have fully covered the request i.e. batchSize has reached 0, we dont need to
                // go further and kick off the encoder hydration TP task, as its not needed, so we return early now.
                // This way we only absorb the cell 0 avalanche, and the encoder catch-up cost when we *absolutely* must.
                return symbols.AsImmutable();
            }
        }

        // Otherwise, we need to initialize the encoder and produce the remaining symbols (or all if the sketch is not used).
        if (state.Encoder is null)
        {
            if (state.HydrationTask is null)
            {
                int targetOffset = state.Offset;

                // We offload the encoder symbol production to a TP thread to avoid blocking the grain thread,
                // as it may take a while to catch up the encoder with a large number of mutations.

                state.HydrationTask = Task.Run(() =>
                {
                    var encoder = new RibltEncoder();

                    // We start the encoder from the target offset, which may be beyond the sketch size,
                    // to ensure we produce the correct symbols. Essentially we fast-forward the encoder to the desired offset,
                    // skipping over any symbols that have already been created (and served on a previous batch round) in the sketch.

                    localCache.ForEachMutation((encoder, targetOffset), (state, mutation) => 
                        state.encoder.AddMutationAtOffset(mutation, state.targetOffset));

                    return encoder;
                });
            }

            if (!state.HydrationTask.IsCompleted)
            {
                // We return immediately so we dont block this observer's queue (giving pings a chance to process).
                // We return the 'symbols' list exactly as-is, which handles two scenarios:

                // 1) Partial Batch: If the fast-path above exhausted the remaining sketch, 'symbols' contains those 
                //    valid sketch symbols. We return them now so the caller can make progress while we hydrate the encoder.

                // 2) Empty Batch: If the sketch was already exhausted on a previous call, 'symbols' is empty.
                //    The caller receives an empty list of symbols, therefore it yields its own thread, and retries a tiny bit later.

                return symbols.AsImmutable();
            }

            state.Encoder = await state.HydrationTask;
            state.HydrationTask = null;

            // If there is a sketch, it will serve the symbols up to its max size. And the encoder we just fast-forwarded
            // will now begin producing an "infinite" (until the peer has enough to fully reconcile) stream of symbols.
            // If this is done within this batchSize, than this encoder will be GC'd and the peer has caught up. So a next
            // reconcilliation round will fully be served by the sketch (since the set diff will be 0 or very small
            // (new cahce updates in between). Otherwise, if another batch round is needed, the StreamState.Offset will point
            // past the sketch and the encoder will not be null, so we just continue producing more symbols for the new batch round.
            // Etc...until the peer is fully reconcilled!
        }

        const int MaxTimeSliceMs = 2;

        var startTime = Stopwatch.GetTimestamp();

        for (int i = 0; i < batchSize; i++)
        {
            var symbol = state.Encoder.ProduceNextSymbol();

            symbols.Add(symbol);
            state.Offset++;

            if ((i & 255) == 0 && Stopwatch.GetElapsedTime(startTime).TotalMilliseconds >= MaxTimeSliceMs)
            {
                // We periodically yield to the scheduler to prevent hogging the CPU on massive caches,
                // ensuring this grain remains a good neighbor to other grains.

                // The R-IBLT PRNG math dictates that ALL cache items initially map to index 0. 
                // This causes a "Cell 0 Avalanche" where every single cache item must be sifted through the min-heap 
                // on the very first call to ProduceNextSymbol(). 
                // Because of the tiered approach, this massive sorting spike is almost always absorbed 
                // by the background Task.Run during the fast-forward phase.

                // However, if the sketch is disabled (MaterializedSketchSize = 0) or we encounter dense heap rebalancing
                // later in the stream, we still need a reliable safety mechanism. 
                // By checking (i & 255) == 0, we evaluate the stopwatch on the very first iteration (i = 0),
                // thereby instantly catching any initial avalanche, and then every 256 iterations thereafter.

                // This creates a "sweet spot":
                //  - We yield at around ~2[ms] of actual CPU time per execution slice.
                //  - We dilute the overhead of calling the stopwatch and yielding.
                //  - We maximize throughput while preventing Orleans grain starvation.

                await Task.Yield();
                startTime = Stopwatch.GetTimestamp(); // Restart the watch for the next round.
            }
        }

        return symbols.AsImmutable();
    }

    public ValueTask CloseSymbolStream(Guid streamId)
    {
        if (_activeStreams.Remove(streamId, out var state) && state.Encoder is { } encoder)
        {
            encoder.Reset();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Immutable<List<CacheMutation>>> GetMutations(List<Guid> mutationIds) =>
        new(new Immutable<List<CacheMutation>>([.. mutationIds.Select(localCache.GetMutation).Where(m => m != null).Select(m => m!)]));

    public ValueTask ApplyMutations(List<CacheMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            localCache.ApplyMutation(mutation);
        }

        return ValueTask.CompletedTask;
    }
}