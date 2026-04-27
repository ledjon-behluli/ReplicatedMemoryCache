namespace Ledjon.ReplicatedMemoryCache;

public sealed class ReplicatedMemoryCacheOptions
{
    internal static readonly TimeSpan LivenessPeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the node picks a random peer to gossip and reconcile cache state.
    /// </summary>
    /// <remarks>
    /// This dictates the speed of the epidemic gossip protocol.
    /// Lowering this value decreases the time it takes for a cache mutation (put/remove) 
    /// to globally replicate across the entire cluster, but increases background network traffic.
    /// Because the reconciliation uses a highly compressed data structure, the network payload is typically
    /// extremely small making aggressive sub-second propagation feasible for most environments.
    /// </remarks>
    public TimeSpan ReconciliationPeriod { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The period of idleness after which the registry considers a node suspected / failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This dictates how quickly the cluster reacts to a disconnected node.
    /// By default, if a node hasnt pinged the registry in 30 seconds, the cluster initiates indirect probing. 
    /// If it remains unresponsive for 60 seconds (2x this period), it is hard-evicted.
    /// </para>
    /// <para>
    /// To ensure stability, the local node will automatically send heartbeats to the registry at an interval 
    /// of <c><see cref="NodeLivenessPeriod"/> / 3</c>. Keep this value significantly larger than 
    /// <see cref="ReconciliationPeriod"/> to avoid registry bottlenecking.
    /// </para>
    /// </remarks>
    public TimeSpan NodeLivenessPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the local cache physically removes expired mutations from memory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When a cache item's TTL is reached, it is 'logically' evicted immediately, 
    /// meaning <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache.TryGetValue(object, out object?)"/>
    /// will immediately return <c>false</c>. However, the item's underlying mutation record is kept in memory to prevent
    /// so called "zombie resurrections" during gossip (see <see cref="ExpiredMutationGracePeriod"/>).
    /// This period dictates how often a background sweep runs to find mutations that have fully 
    /// outlived their grace period and physically remove them to free up memory.
    /// </para>
    /// <para>Keep this value significantly lower than the <see cref="ExpiredMutationGracePeriod"/>.</para>
    /// </remarks>
    public TimeSpan MemorySweepingPeriod { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to keep logically expired cache items in memory before physically removing them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If node A physically removes an expired item at exactly 12:00 PM, 
    /// but node B has a slight clock drift (e.g., its clock reads 11:59:59 AM), node B will still have the item.
    /// During the next gossip round, node B's decoder will determine that node A is missing the item, 
    /// and it will push the expired item back to node A, effectively resurrecting it.
    /// </para>
    /// <para>
    /// To prevent this, expired items are logically removed from the cache, but kept in the mutation journal 
    /// for this grace period. When node B tries to push the item, node A will recognize that it is expired, and safely ignore the push.
    /// </para>
    /// <para>
    /// This value should be large enough to account for maximum expected clock drift across the cluster, 
    /// plus the time it takes for a full cluster gossip convergence.
    /// </para>
    /// </remarks>
    public TimeSpan ExpiredMutationGracePeriod { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The number of consecutive registry checks a node must miss its heartbeat before triggering indirect probing.
    /// </summary>
    public int MissedHeartbeatsBeforeIndirectProbing { get; set; } = 1;

    /// <summary>
    /// The maximum number of R-IBLT symbols to pull from a peer during a single gossip protocol exchange.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Because the R-IBLT generator produces an infinite stream of probabilistic symbols, this setting acts 
    /// as a circuit breaker. It prevents a node from getting into an excessively long reconciliation session 
    /// if two nodes have drifted massively out of sync (i.e. partitioned for days with millions of disjoint keys).
    /// </para>
    /// <para>
    /// By default, it is set to practically 'infinite', meaning the decoder will confidently stream as many symbols as it
    /// needs to resolve the exact difference. If you lower this value, nodes with massive drift will abort 
    /// the sync early and attempt to resolve the remaining differences on a subsequent protocol exchange.
    /// </para>
    /// </remarks>
    public int MaxSymbolsToPull { get; set; } = int.MaxValue;

    /// <summary>
    /// The number of R-IBLT symbols to batch together in a single network round-trip during one gossip round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default size of <b>256</b> results in an <c>~8[KB]</c> payload (each symbol is worth 32 bytes),
    /// which is fast to serialize and should fit within standard network buffers. 
    /// The default batch size can heal <c>~190</c> missing cache entries, per round-trip!
    /// </para>
    /// <para>
    /// Favor larger batch sizes if you expect massively drifted nodes, but beware of GC spikes from very large payloads.
    /// </para>
    /// </remarks>
    public int SymbolStreamBatchSize { get; set; } = 256;

    /// <summary>
    /// The maximum number of cache mutations sent/received per-RPC invocation during the final stage of the protocol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once the R-IBLT decoder resolves exactly which entries are missing, this configuration determines the collection
    /// capacity limit for fetching/pushing batches over the network.
    /// </para>
    /// <para>
    /// Setting this to a predictable value prevents the creation of oversized transient collections that can cause fragmentation on the LOH. 
    /// </para>
    /// <para>
    /// If your cache entries have exceptionally large payloads, consider lowering this value to optimize network serialization and minimize memory spikes.
    /// </para>
    /// </remarks>
    public int MutationTransferBatchSize { get; set; } = 1024;

    /// <summary>
    /// The size of a pre-materialized IBLT sketch of a wider, infinite stream.
    /// This acts as a fast-access buffer to serve the majority of gossip syncs heavily dumping memory usage and CPU cycles.
    /// The default size of 8192 requires ~256KB of memory and can "instantly" heal ~6k set differences.
    /// </summary>
    /// <remarks>
    /// <para>Adjust this value based on your cluster's size and expected drift.</para>
    /// <para>The higher the sketch size is, the higher penalty you have to pay upon every cache entry update.</para>
    /// <para>Setting this to 0 means no sketch is used at all.</para>
    /// </remarks>
    public int MaterializedSketchSize { get; set; } = 8192;
}