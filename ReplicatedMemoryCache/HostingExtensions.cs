using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ledjon.ReplicatedMemoryCache;

internal record NamedReplicationScope(string Value);

/// <summary>
/// Extensions methods for the replicated in-memory cache.
/// </summary>
public static class HostingExtensions
{
    /// <summary>
    /// Registers the replicated in-memory cache.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="scopeName">A named scope to isolate multiple independent cache replicas within the same application.</param>
    /// <param name="configureOptions">An optional action to configure the <see cref="ReplicatedMemoryCacheOptions"/>.</param>
    public static IServiceCollection AddMemoryCacheReplication(
        this IServiceCollection services, string scopeName, Action<ReplicatedMemoryCacheOptions>? configureOptions = null) =>
            services.AddMemoryCacheReplication<OrleansCacheEntrySerializer>(scopeName, configureOptions);

    /// <summary>
    /// Registers the replicated in-memory cache, using a custom <see cref="ICacheEntrySerializer"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="scopeName">A named scope to isolate multiple independent cache replicas within the same application.</param>
    /// <param name="configureOptions">An optional action to configure the <see cref="ReplicatedMemoryCacheOptions"/>.</param>
    public static IServiceCollection AddMemoryCacheReplication<TSerializer>(
        this IServiceCollection services, string scopeName, Action<ReplicatedMemoryCacheOptions>? configureOptions = null)
            where TSerializer : class, ICacheEntrySerializer
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeName, nameof(scopeName));

        if (configureOptions is { } options)
        {
            services.Configure(scopeName, options);
        }

        services.AddSingleton(new NamedReplicationScope(scopeName));

        services.AddKeyedSingleton<LocalNodeCache>(scopeName, (sp, key) =>
            new LocalNodeCache(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptionsMonitor<ReplicatedMemoryCacheOptions>>().Get((string)key!)));

        services.AddKeyedSingleton<ICacheEntrySerializer, TSerializer>(scopeName);

        services.AddKeyedSingleton<IReplicatedMemoryCache, ReplicatedMemoryCache>(scopeName, (sp, key) =>
            new ReplicatedMemoryCache(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredKeyedService<LocalNodeCache>(key),
                sp.GetRequiredKeyedService<ICacheEntrySerializer>(key)));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ReplicationService>());

        return services;
    }
}
