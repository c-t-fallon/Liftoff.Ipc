using System.Text.Json;
using System.Threading.Channels;
using Liftoff.Ipc.Internal;

namespace Liftoff.Ipc;

public sealed class IpcSubscription<TEvent> : IAsyncEnumerable<TEvent>, IAsyncDisposable
    where TEvent : IIpcEvent
{
    private readonly Guid _subscriptionId;
    private readonly Func<Guid, ValueTask> _unsubscribe;
    private readonly Channel<TEvent> _events = Channel.CreateUnbounded<TEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private int _disposed;

    internal IpcSubscription(Guid subscriptionId, Func<Guid, ValueTask> unsubscribe)
    {
        _subscriptionId = subscriptionId;
        _unsubscribe = unsubscribe;
    }

    internal TaskCompletionSource<bool> Accepted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<bool> Unsubscribed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerator<TEvent> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    internal void Publish(JsonElement data)
    {
        var item = data.Deserialize<TEvent>(IpcProtocol.JsonOptions)
            ?? throw new InvalidDataException($"The event contained no {typeof(TEvent).Name} payload.");
        _events.Writer.TryWrite(item);
    }

    internal void Fail(Exception exception)
    {
        Accepted.TrySetException(exception);
        Unsubscribed.TrySetException(exception);
        _events.Writer.TryComplete(exception);
    }

    internal void Complete() => _events.Writer.TryComplete();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Complete();
        await _unsubscribe(_subscriptionId);
    }
}

internal interface ISubscriptionState
{
    TaskCompletionSource<bool> Accepted { get; }
    TaskCompletionSource<bool> Unsubscribed { get; }
    void Publish(JsonElement data);
    void Fail(Exception exception);
    void Complete();
}

internal sealed class SubscriptionState<TEvent>(IpcSubscription<TEvent> subscription) : ISubscriptionState
    where TEvent : IIpcEvent
{
    public TaskCompletionSource<bool> Accepted => subscription.Accepted;
    public TaskCompletionSource<bool> Unsubscribed => subscription.Unsubscribed;
    public void Publish(JsonElement data) => subscription.Publish(data);
    public void Fail(Exception exception) => subscription.Fail(exception);
    public void Complete() => subscription.Complete();
}
