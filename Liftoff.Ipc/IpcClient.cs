using Liftoff.Ipc.Internal;
using System.Collections.Concurrent;

namespace Liftoff.Ipc;

public sealed class IpcClient : IIpcClient
{
    private readonly IIpcTransport _transport;
    private readonly TimeSpan _acknowledgementTimeout;
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, ISubscriptionState> _subscriptions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _readerTask;
    private int _disposed;

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
        await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new IpcClient(transport, options.AcknowledgementTimeout);
    }

    public static Task<IpcClient> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(pipeName, new IpcClientOptions(), cancellationToken);

    public static Task<IpcClient> ConnectAsync(
        string pipeName,
        IpcClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionArguments(pipeName, options);
        return ConnectAsync(
            new NamedPipeTransport(pipeName, options),
            options,
            cancellationToken);
    }

    public static Task<IpcClient> ConnectAsync(
        IpcSession session,
        CancellationToken cancellationToken = default) =>
        ConnectAsync(session, new IpcClientOptions(), cancellationToken);

    public static Task<IpcClient> ConnectAsync(
        IpcSession session,
        IpcClientOptions options,
        CancellationToken cancellationToken = default)
    {
        if (session is null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        ValidateConnectionArguments(session.PipeName, options);
        return ConnectAsync(
            new NamedPipeTransport(
                session.PipeName,
                options,
                session.CopyAuthenticationKey()),
            options,
            cancellationToken);
    }

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

        var arguments = IpcSerializer.Serialize(request, request.GetType());
        var execute = new ExecuteRequest(ContractName.For(request.GetType()), arguments);

        try
        {
            await _transport.SendAsync(
                IpcEnvelope.Create(MessageTypes.ExecuteRequest, requestId, execute),
                cancellationToken).ConfigureAwait(false);
            await AsyncCompatibility.WaitWithTimeoutAsync(
                pending.Accepted.Task,
                _acknowledgementTimeout,
                cancellationToken).ConfigureAwait(false);

            var result = await AsyncCompatibility.WaitWithCancellationAsync(
                pending.Completion.Task,
                cancellationToken).ConfigureAwait(false);
            return IpcSerializer.Deserialize<TResponse>(result);
        }
        catch (OperationCanceledException)
        {
            await TrySendCancellationAsync(requestId).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
            await AsyncCompatibility.WaitWithTimeoutAsync(
                state.Accepted.Task,
                _acknowledgementTimeout,
                cancellationToken).ConfigureAwait(false);
            return subscription;
        }
        catch
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await AsyncCompatibility.CancelAsync(_lifetime).ConfigureAwait(false);

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
        await _transport.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }

        _lifetime.Dispose();
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? disconnectError = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await _transport.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

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
                IpcEnvelope.CreateEmpty(MessageTypes.CancelRequest, requestId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
    }

    private static void ValidateConnectionArguments(string pipeName, IpcClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("The pipe name cannot be empty.", nameof(pipeName));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.AcknowledgementTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The acknowledgement timeout must be positive.",
                nameof(options));
        }

        if (options.AuthenticationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The authentication timeout must be positive.",
                nameof(options));
        }
    }

    private async Task RemoveSubscriptionAsync(Guid subscriptionId)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            return;
        }

        try
        {
            await _transport.SendAsync(
                IpcEnvelope.CreateEmpty(MessageTypes.UnsubscribeRequest, subscriptionId),
                CancellationToken.None).ConfigureAwait(false);
            await AsyncCompatibility.WaitWithTimeoutAsync(
                subscription.Unsubscribed.Task,
                _acknowledgementTimeout,
                CancellationToken.None).ConfigureAwait(false);
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
        public TaskCompletionSource<byte[]> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<IpcProgress>? Progress { get; } = progress;

        public void Fail(Exception exception)
        {
            Accepted.TrySetException(exception);
            Completion.TrySetException(exception);
        }
    }
}
