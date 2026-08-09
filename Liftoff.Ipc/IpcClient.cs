using Liftoff.Ipc.Internal;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Liftoff.Ipc;

public sealed class IpcClient : IIpcClient
{
    private readonly IIpcTransport _transport;
    private readonly TimeSpan _acknowledgementTimeout;
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, ISubscriptionState> _subscriptions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readerTask;

    private IpcClient(IIpcTransport transport, TimeSpan acknowledgementTimeout)
    {
        _transport = transport;
        _acknowledgementTimeout = acknowledgementTimeout;
        _readerTask = ReadLoopAsync(_lifetime.Token);
    }

    internal static async Task<IpcClient> ConnectAsync(
        IIpcTransport transport,
        IpcClientOptions options,
        CancellationToken cancellationToken)
    {
        await transport.ConnectAsync(cancellationToken);
        return new IpcClient(transport, options.AcknowledgementTimeout);
    }

    public static Task<IpcClient> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(pipeName, new IpcClientOptions(), cancellationToken);

    public static Task<IpcClient> ConnectAsync(
        string pipeName,
        IpcClientOptions options,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(new NamedPipeTransport(pipeName), options, cancellationToken);

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public async Task<TResponse> RequestAsync<TResponse>(
        IIpcRequest<TResponse> request,
        IProgress<IpcProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid();
        var pending = new PendingRequest(progress);
        if (!_pending.TryAdd(requestId, pending))
        {
            throw new InvalidOperationException($"Duplicate request ID {requestId}.");
        }

        var arguments = JsonSerializer.SerializeToElement(request, request.GetType(), IpcProtocol.JsonOptions);
        var execute = new ExecuteRequest(ContractName.For(request.GetType()), arguments);

        try
        {
            await _transport.SendAsync(
                IpcEnvelope.Create(MessageTypes.ExecuteRequest, requestId, execute),
                cancellationToken);
            await pending.Accepted.Task.WaitAsync(_acknowledgementTimeout, cancellationToken);

            var result = await pending.Completion.Task.WaitAsync(cancellationToken);
            return result.Deserialize<TResponse>(IpcProtocol.JsonOptions)
                ?? throw new InvalidDataException($"The server returned no {typeof(TResponse).Name} result.");
        }
        catch (OperationCanceledException)
        {
            await TrySendCancellationAsync(requestId);
            throw;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public async Task<IpcSubscription<TEvent>> SubscribeAsync<TEvent>(
        CancellationToken cancellationToken = default)
        where TEvent : IIpcEvent
    {
        var subscriptionId = Guid.NewGuid();
        var subscription = new IpcSubscription<TEvent>(subscriptionId, RemoveSubscriptionAsync);
        var state = new SubscriptionState<TEvent>(subscription);
        if (!_subscriptions.TryAdd(subscriptionId, state))
        {
            throw new InvalidOperationException($"Duplicate subscription ID {subscriptionId}.");
        }

        try
        {
            await _transport.SendAsync(
                IpcEnvelope.Create(
                    MessageTypes.SubscribeRequest,
                    subscriptionId,
                    new SubscribeRequest(ContractName.For<TEvent>())),
                cancellationToken);
            await state.Accepted.Task.WaitAsync(_acknowledgementTimeout, cancellationToken);
            return subscription;
        }
        catch
        {
            await subscription.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();

        var disposed = new IpcDisconnectedException("The IPC client was disposed.");
        foreach (var pending in _pending.Values)
        {
            pending.Fail(disposed);
        }

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Fail(disposed);
        }

        _subscriptions.Clear();
        await _transport.DisposeAsync();

        try
        {
            await _readerTask;
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }

        _lifetime.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? disconnectError = null;
        try
        {
            await foreach (var message in _transport.ReadAllAsync(cancellationToken))
            {
                Dispatch(message);
            }

            disconnectError = new IpcDisconnectedException("The server closed the named pipe.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            disconnectError = new IpcDisconnectedException("The named-pipe connection was lost.", exception);
        }
        finally
        {
            if (disconnectError is not null)
            {
                foreach (var pending in _pending.Values)
                {
                    pending.Fail(disconnectError);
                }

                foreach (var subscription in _subscriptions.Values)
                {
                    subscription.Fail(disconnectError);
                }
            }
        }
    }

    private void Dispatch(IpcEnvelope message)
    {
        if (message.Type == MessageTypes.Heartbeat)
        {
            LastHeartbeatAt = message.ReadPayload<Heartbeat>().SentAt;
            return;
        }

        if (message.RequestId is not Guid requestId)
        {
            return;
        }

        if (message.Type == MessageTypes.SubscriptionAccepted
            && _subscriptions.TryGetValue(requestId, out var accepted))
        {
            accepted.Accepted.TrySetResult(true);
            return;
        }

        if (message.Type == MessageTypes.SubscriptionRejected
            && _subscriptions.TryRemove(requestId, out var rejected))
        {
            rejected.Fail(new IpcRemoteException(message.ReadPayload<SubscriptionRejected>().Error));
            return;
        }

        if (message.Type == MessageTypes.EventPublished
            && _subscriptions.TryGetValue(requestId, out var eventSubscription))
        {
            eventSubscription.Publish(message.ReadPayload<EventPublished>().Data);
            return;
        }

        if (message.Type == MessageTypes.UnsubscriptionAccepted
            && _subscriptions.TryGetValue(requestId, out var unsubscribed))
        {
            unsubscribed.Unsubscribed.TrySetResult(true);
            return;
        }

        if (!_pending.TryGetValue(requestId, out var pending))
        {
            return;
        }

        switch (message.Type)
        {
            case MessageTypes.RequestAccepted:
                pending.Accepted.TrySetResult(true);
                break;
            case MessageTypes.OperationProgress:
                var update = message.ReadPayload<OperationProgress>();
                pending.Progress?.Report(new IpcProgress(update.Percent, update.Message));
                break;
            case MessageTypes.OperationCompleted:
                pending.Completion.TrySetResult(message.ReadPayload<OperationCompleted>().Result);
                break;
            case MessageTypes.OperationFailed:
                pending.Fail(new IpcRemoteException(message.ReadPayload<OperationFailed>().Error));
                break;
            case MessageTypes.OperationCancelled:
                pending.Fail(new OperationCanceledException(message.ReadPayload<OperationCancelled>().Reason));
                break;
        }
    }

    private async Task TrySendCancellationAsync(Guid requestId)
    {
        try
        {
            await _transport.SendAsync(
                IpcEnvelope.Create(MessageTypes.CancelRequest, requestId, new { }),
                CancellationToken.None);
        }
        catch { }
    }

    private async ValueTask RemoveSubscriptionAsync(Guid subscriptionId)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            return;
        }

        try
        {
            await _transport.SendAsync(
                IpcEnvelope.Create(MessageTypes.UnsubscribeRequest, subscriptionId, new { }),
                CancellationToken.None);
            await subscription.Unsubscribed.Task.WaitAsync(_acknowledgementTimeout);
        }
        catch { }
        finally
        {
            _subscriptions.TryRemove(subscriptionId, out _);
            subscription.Complete();
        }
    }

    private sealed class PendingRequest(IProgress<IpcProgress>? progress)
    {
        public TaskCompletionSource<bool> Accepted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<IpcProgress>? Progress { get; } = progress;

        public void Fail(Exception exception)
        {
            Accepted.TrySetException(exception);
            Completion.TrySetException(exception);
        }
    }
}
