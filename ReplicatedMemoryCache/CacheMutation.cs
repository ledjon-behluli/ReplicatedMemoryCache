namespace Ledjon.ReplicatedMemoryCache;

[GenerateSerializer]
[Alias("Ledjon.ReplicatedMemoryCache.CacheMutation")]
internal class CacheMutation
{
    [Id(0)] public required Guid MutationId { get; init; }
    [Id(1)] public required VersionTag Version { get; init; }
    [Id(2)] public required string CacheKey { get; init; }
    [Id(3)] public required bool IsTombstone { get; init; }
    [Id(4)] public required byte[]? PayloadBytes { get; init; }
    [Id(5)] public required uint PayloadHash { get; init; }
    [Id(6)] public required DateTimeOffset? ExpiresAt { get; init; }
}