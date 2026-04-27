using System.Collections.Concurrent;

namespace Ledjon.ReplicatedMemoryCache.StressTests;

public class HotKeySet(int runId)
{
    private const int MaxTracked = 20_000;

    private long _totalWrites = 0;
    private readonly ConcurrentQueue<string> _keys = [];

    public int TrackedCount => _keys.Count;
    public long TotalWrites => _totalWrites;

    public string GetRandomKey() => $"r{runId}_k{Random.Shared.Next(50_000)}";

    public void RecordWrite(string key)
    {
        Interlocked.Increment(ref _totalWrites);

        if (_keys.Count < MaxTracked)
        {
            _keys.Enqueue(key);
        }
    }

    public IReadOnlyList<string> GetSample(int count)
    {
        var list = _keys.ToList();

        if (list.Count == 0)
        {
            return [.. Enumerable.Range(0, count).Select(_ => GetRandomKey())];
        }

        return [.. list.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(count, list.Count))];
    }
}
