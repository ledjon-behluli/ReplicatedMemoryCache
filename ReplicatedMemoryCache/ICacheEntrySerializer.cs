using Orleans.Serialization;

namespace Ledjon.ReplicatedMemoryCache;

/// <summary>
/// Defines a contract for serializing and deserializing objects in the replicated in-memory cache.
/// </summary>
public interface ICacheEntrySerializer
{
    /// <summary>
    /// Serializes a cache entry into a byte array for network replication.
    /// </summary>
    /// <param name="obj">The object to serialize.</param>
    /// <returns>A byte array representing the serialized object.</returns>
    byte[] Serialize(object obj);

    /// <summary>
    /// Deserializes a byte array back into its original object form.
    /// </summary>
    /// <param name="bytes">The byte array payload received from the network.</param>
    /// <returns>The deserialized object, or <see langword="null"/> if deserialization fails.</returns>
    object? Deserialize(byte[] bytes);
}

internal class OrleansCacheEntrySerializer(Serializer serializer) : ICacheEntrySerializer
{
    public byte[] Serialize(object obj) => serializer.SerializeToArray(obj);
    public object? Deserialize(byte[] bytes) => serializer.Deserialize<object>(bytes);
}