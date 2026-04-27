using Orleans.Serialization;

namespace Ledjon.ReplicatedMemoryCache;

public interface ICacheEntrySerializer
{
    byte[] Serialize(object obj);
    object? Deserialize(byte[] bytes);
}

internal class OrleansCacheEntrySerializer(Serializer serializer) : ICacheEntrySerializer
{
    public byte[] Serialize(object obj) => serializer.SerializeToArray(obj);
    public object? Deserialize(byte[] bytes) => serializer.Deserialize<object>(bytes);
}