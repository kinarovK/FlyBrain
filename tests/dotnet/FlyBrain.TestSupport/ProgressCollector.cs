namespace FlyBrain.TestSupport;

/// <summary>
/// Collects progress reports synchronously, on the reporting thread.
/// <see cref="Progress{T}"/> posts each report to the thread pool (there is no
/// synchronization context in tests), so a report can still be pending when the
/// awaited operation returns and counts become racy. Use this instead.
/// </summary>
public sealed class ProgressCollector<T> : IProgress<T>
{
    private readonly List<T> items = [];

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (items)
            {
                return items.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (items)
            {
                return items.Count;
            }
        }
    }

    public void Report(T value)
    {
        lock (items)
        {
            items.Add(value);
        }
    }
}
