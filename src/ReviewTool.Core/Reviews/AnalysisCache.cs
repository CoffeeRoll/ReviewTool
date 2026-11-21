using System.Collections.Concurrent;

namespace ReviewTool.Core.Reviews;

/// <summary>
/// Tiny bounded memo for expensive git analyses whose inputs are immutable (keyed by SHAs).
/// Failed computations are evicted so they can be retried.
/// </summary>
internal sealed class AnalysisCache<T>(int capacity = 500)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _items = new();

    public Task<T> GetOrAdd(string key, Func<Task<T>> factory)
    {
        if (_items.Count > capacity) _items.Clear();
        var lazy = _items.GetOrAdd(key, _ => new Lazy<Task<T>>(factory));
        var task = lazy.Value;
        if (task.IsFaulted || task.IsCanceled) _items.TryRemove(key, out _);
        else task.ContinueWith(t => _items.TryRemove(key, out _), TaskContinuationOptions.NotOnRanToCompletion);
        return task;
    }
}
