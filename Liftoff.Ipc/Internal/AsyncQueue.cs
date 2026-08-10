namespace Liftoff.Ipc.Internal;

internal sealed class AsyncQueue<T>
{
    private readonly Queue<T> _items = new();
    private readonly SemaphoreSlim _available = new(0);
    private Exception? _completionError;
    private bool _completed;

    public bool TryEnqueue(T item)
    {
        lock (_items)
        {
            if (_completed)
            {
                return false;
            }

            _items.Enqueue(item);
        }

        _available.Release();
        return true;
    }

    public async Task<AsyncQueueRead<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_items)
            {
                if (_items.Count != 0)
                {
                    return new AsyncQueueRead<T>(true, _items.Dequeue());
                }

                if (_completed)
                {
                    if (_completionError is not null)
                    {
                        throw _completionError;
                    }

                    return new AsyncQueueRead<T>(false, default!);
                }
            }
        }
    }

    public void Complete(Exception? error = null)
    {
        lock (_items)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _completionError = error;
        }

        _available.Release();
    }
}

internal readonly struct AsyncQueueRead<T>(bool hasItem, T item)
{
    public bool HasItem { get; } = hasItem;
    public T Item { get; } = item;
}
