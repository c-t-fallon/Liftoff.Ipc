using Liftoff.Ipc.Internal;

namespace Liftoff.Ipc;

public sealed class IpcSubscription<TEvent> : IDisposable
    where TEvent : IIpcEvent
{
    private readonly Guid _subscriptionId;
    private readonly Func<Guid, Task> _unsubscribe;
    private readonly AsyncQueue<TEvent> _events = new();
    private int _disposed;

    internal IpcSubscription(Guid subscriptionId, Func<Guid, Task> unsubscribe)
    {
        _subscriptionId = subscriptionId;
        _unsubscribe = unsubscribe;
    }

    internal TaskCompletionSource<bool> Accepted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<bool> Unsubscribed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Enumerator GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new(_events, cancellationToken);

    public Enumerable WithCancellation(CancellationToken cancellationToken) =>
        new(this, cancellationToken);

    public async Task<TEvent> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _events.ReadAsync(cancellationToken).ConfigureAwait(false);
        return result.HasItem
            ? result.Item
            : throw new EndOfStreamException("The IPC subscription has completed.");
    }

    internal void Publish(byte[] data) =>
        _events.TryEnqueue(IpcSerializer.Deserialize<TEvent>(data));

    internal void Fail(Exception exception)
    {
        Accepted.TrySetException(exception);
        Unsubscribed.TrySetException(exception);
        _events.Complete(exception);
    }

    internal void Complete() => _events.Complete();

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Complete();
        await _unsubscribe(_subscriptionId).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    public sealed class Enumerator
    {
        private readonly AsyncQueue<TEvent> _events;
        private readonly CancellationToken _cancellationToken;

        internal Enumerator(AsyncQueue<TEvent> events, CancellationToken cancellationToken)
        {
            _events = events;
            _cancellationToken = cancellationToken;
        }

        public TEvent Current { get; private set; } = default!;

        public async Task<bool> MoveNextAsync()
        {
            var result = await _events.ReadAsync(_cancellationToken).ConfigureAwait(false);
            if (!result.HasItem)
            {
                return false;
            }

            Current = result.Item;
            return true;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    public readonly struct Enumerable(
        IpcSubscription<TEvent> subscription,
        CancellationToken cancellationToken)
    {
        public Enumerator GetAsyncEnumerator() =>
            subscription.GetAsyncEnumerator(cancellationToken);
    }
}

internal interface ISubscriptionState
{
    TaskCompletionSource<bool> Accepted { get; }
    TaskCompletionSource<bool> Unsubscribed { get; }
    void Publish(byte[] data);
    void Fail(Exception exception);
    void Complete();
}

internal sealed class SubscriptionState<TEvent>(IpcSubscription<TEvent> subscription) : ISubscriptionState
    where TEvent : IIpcEvent
{
    public TaskCompletionSource<bool> Accepted => subscription.Accepted;
    public TaskCompletionSource<bool> Unsubscribed => subscription.Unsubscribed;
    public void Publish(byte[] data) => subscription.Publish(data);
    public void Fail(Exception exception) => subscription.Fail(exception);
    public void Complete() => subscription.Complete();
}
