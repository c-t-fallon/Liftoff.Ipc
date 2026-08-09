using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Liftoff.Ipc.Internal;

namespace Liftoff.Ipc;

public sealed class IpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly IpcServerOptions _options;
    private readonly ConcurrentDictionary<string, IRequestHandlerInvoker> _handlers = new();
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;

    private IpcServer(string pipeName, IpcServerOptions options)
    {
        _pipeName = pipeName;
        _options = options;
    }

    public static IpcServer Create(
        string pipeName,
        Action<IpcServerOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        var options = new IpcServerOptions();
        configure?.Invoke(options);
        return new IpcServer(pipeName, options);
    }

    public void RegisterHandler<TRequest, TResponse>(
        IIpcRequestHandler<TRequest, TResponse> handler)
        where TRequest : IIpcRequest<TResponse> =>
        RegisterInvoker(new RequestHandlerInvoker<TRequest, TResponse>(handler));

    public void RegisterHandler<TRequest, TResponse>(
        Func<TRequest, IpcRequestContext, CancellationToken, ValueTask<TResponse>> handler)
        where TRequest : IIpcRequest<TResponse> =>
        RegisterHandler(new DelegateRequestHandler<TRequest, TResponse>(handler));

    public void RegisterHandlersFromAssembly(
        Assembly assembly,
        Func<Type, object>? handlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var implementationType in assembly.GetTypes()
                     .Where(type => type is { IsAbstract: false, IsInterface: false }))
        {
            var handlerInterfaces = implementationType.GetInterfaces()
                .Where(type => type.IsGenericType
                    && type.GetGenericTypeDefinition() == typeof(IIpcRequestHandler<,>))
                .ToArray();

            if (handlerInterfaces.Length == 0)
            {
                continue;
            }

            var handler = handlerFactory?.Invoke(implementationType)
                ?? Activator.CreateInstance(implementationType)
                ?? throw new IpcConfigurationException(
                    $"Could not construct IPC handler '{implementationType.FullName}'.");

            foreach (var handlerInterface in handlerInterfaces)
            {
                var arguments = handlerInterface.GetGenericArguments();
                var invokerType = typeof(RequestHandlerInvoker<,>).MakeGenericType(arguments);
                var invoker = (IRequestHandlerInvoker?)Activator.CreateInstance(invokerType, handler)
                    ?? throw new IpcConfigurationException(
                        $"Could not register IPC handler '{implementationType.FullName}'.");
                RegisterInvoker(invoker);
            }
        }
    }

    public void RegisterHandlersFromAssemblyContaining<TMarker>(
        Func<Type, object>? handlerFactory = null) =>
        RegisterHandlersFromAssembly(typeof(TMarker).Assembly, handlerFactory);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_runTask is not null)
            {
                throw new InvalidOperationException("The IPC server has already been started.");
            }

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runTask = RunAsync(_lifetime.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? runTask;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_lifetime is null || _runTask is null)
            {
                return;
            }

            await _lifetime.CancelAsync();
            runTask = _runTask;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        try
        {
            await runTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async ValueTask<int> PublishAsync<TEvent>(
        TEvent data,
        CancellationToken cancellationToken = default)
        where TEvent : IIpcEvent
    {
        var eventData = JsonSerializer.SerializeToElement(data, IpcProtocol.JsonOptions);
        var eventContract = ContractName.For<TEvent>();
        var delivered = 0;

        foreach (var session in _sessions.Values)
        {
            delivered += await session.PublishAsync(eventContract, eventData, cancellationToken);
        }

        return delivered;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime?.Dispose();
        _lifecycleGate.Dispose();
    }

    private void RegisterInvoker(IRequestHandlerInvoker invoker)
    {
        if (_runTask is not null)
        {
            throw new InvalidOperationException("Handlers must be registered before the server starts.");
        }

        if (!_handlers.TryAdd(invoker.Contract, invoker))
        {
            throw new IpcConfigurationException(
                $"A handler is already registered for '{invoker.RequestType.FullName}'.");
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync(cancellationToken);
            var sessionId = Guid.NewGuid();
            var session = new ClientSession(pipe, _handlers, _options.HeartbeatInterval);
            _sessions.TryAdd(sessionId, session);
            try
            {
                await session.RunAsync(cancellationToken);
            }
            finally
            {
                _sessions.TryRemove(sessionId, out _);
            }
        }
    }

    private sealed class ClientSession(
        NamedPipeServerStream pipe,
        IReadOnlyDictionary<string, IRequestHandlerInvoker> handlers,
        TimeSpan heartbeatInterval)
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly Channel<WorkItem> _workQueue = Channel.CreateUnbounded<WorkItem>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _operations = new();
        private readonly ConcurrentDictionary<Guid, string> _subscriptions = new();

        public async Task RunAsync(CancellationToken serverCancellation)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
            var reader = ReadMessagesAsync(lifetime.Token);
            var worker = ProcessWorkQueueAsync(lifetime.Token);
            var heartbeat = SendHeartbeatsAsync(lifetime.Token);

            try
            {
                await reader;
            }
            catch (IOException) { }
            finally
            {
                await lifetime.CancelAsync();
                _workQueue.Writer.TryComplete();
                foreach (var operation in _operations.Values)
                {
                    try
                    {
                        operation.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The worker completed and disposed this operation after
                        // the concurrent dictionary snapshot was taken.
                    }
                }

                await IgnoreShutdownExceptionAsync(worker);
                await IgnoreShutdownExceptionAsync(heartbeat);
                foreach (var operation in _operations.Values)
                {
                    operation.Dispose();
                }

                _writeGate.Dispose();
            }
        }

        public async Task<int> PublishAsync(
            string eventContract,
            JsonElement data,
            CancellationToken cancellationToken)
        {
            var ids = _subscriptions
                .Where(subscription => subscription.Value == eventContract)
                .Select(subscription => subscription.Key)
                .ToArray();
            var delivered = 0;
            var published = new EventPublished(data);

            foreach (var subscriptionId in ids)
            {
                try
                {
                    await SendAsync(
                        IpcEnvelope.Create(MessageTypes.EventPublished, subscriptionId, published),
                        cancellationToken);
                    delivered++;
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }

            return delivered;
        }

        private async Task ReadMessagesAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await LengthPrefixedJson.ReadAsync(pipe, cancellationToken);
                if (envelope is null)
                {
                    return;
                }

                switch (envelope.Type)
                {
                    case MessageTypes.ExecuteRequest when envelope.RequestId is Guid requestId:
                        await AcceptRequestAsync(
                            requestId,
                            envelope.ReadPayload<ExecuteRequest>(),
                            cancellationToken);
                        break;
                    case MessageTypes.CancelRequest when envelope.RequestId is Guid requestId:
                        if (_operations.TryGetValue(requestId, out var operation))
                        {
                            operation.Cancel();
                        }
                        break;
                    case MessageTypes.SubscribeRequest when envelope.RequestId is Guid subscriptionId:
                        await SubscribeAsync(
                            subscriptionId,
                            envelope.ReadPayload<SubscribeRequest>(),
                            cancellationToken);
                        break;
                    case MessageTypes.UnsubscribeRequest when envelope.RequestId is Guid subscriptionId:
                        _subscriptions.TryRemove(subscriptionId, out _);
                        await SendAsync(
                            IpcEnvelope.Create(
                                MessageTypes.UnsubscriptionAccepted,
                                subscriptionId,
                                new UnsubscriptionAccepted(DateTimeOffset.UtcNow)),
                            cancellationToken);
                        break;
                }
            }
        }

        private async Task AcceptRequestAsync(
            Guid requestId,
            ExecuteRequest request,
            CancellationToken cancellationToken)
        {
            if (!handlers.TryGetValue(request.Contract, out var handler))
            {
                await SendAsync(
                    IpcEnvelope.Create(
                        MessageTypes.OperationFailed,
                        requestId,
                        new OperationFailed($"No handler is registered for '{request.Contract}'.")),
                    cancellationToken);
                return;
            }

            var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!_operations.TryAdd(requestId, operation))
            {
                operation.Dispose();
                await SendAsync(
                    IpcEnvelope.Create(
                        MessageTypes.OperationFailed,
                        requestId,
                        new OperationFailed("Duplicate request ID.")),
                    cancellationToken);
                return;
            }

            await _workQueue.Writer.WriteAsync(
                new WorkItem(requestId, request.Arguments, handler, operation),
                cancellationToken);
            await SendAsync(
                IpcEnvelope.Create(
                    MessageTypes.RequestAccepted,
                    requestId,
                    new RequestAccepted(DateTimeOffset.UtcNow)),
                cancellationToken);
        }

        private async Task SubscribeAsync(
            Guid subscriptionId,
            SubscribeRequest request,
            CancellationToken cancellationToken)
        {
            if (_subscriptions.ContainsKey(subscriptionId))
            {
                await SendAsync(
                    IpcEnvelope.Create(
                        MessageTypes.SubscriptionRejected,
                        subscriptionId,
                        new SubscriptionRejected("Duplicate subscription ID.")),
                    cancellationToken);
                return;
            }

            await SendAsync(
                IpcEnvelope.Create(
                    MessageTypes.SubscriptionAccepted,
                    subscriptionId,
                    new SubscriptionAccepted(request.EventContract, DateTimeOffset.UtcNow)),
                cancellationToken);
            _subscriptions.TryAdd(subscriptionId, request.EventContract);
        }

        private async Task ProcessWorkQueueAsync(CancellationToken cancellationToken)
        {
            await foreach (var work in _workQueue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    var context = new IpcRequestContext((progress, token) =>
                        SendAsync(
                            IpcEnvelope.Create(
                                MessageTypes.OperationProgress,
                                work.RequestId,
                                new OperationProgress(progress.Percent, progress.Message)),
                            token));
                    var result = await work.Handler.InvokeAsync(
                        work.Arguments,
                        context,
                        work.Cancellation.Token);
                    await SendAsync(
                        IpcEnvelope.Create(
                            MessageTypes.OperationCompleted,
                            work.RequestId,
                            new OperationCompleted(result)),
                        work.Cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    await TrySendAsync(
                        IpcEnvelope.Create(
                            MessageTypes.OperationCancelled,
                            work.RequestId,
                            new OperationCancelled("Cancellation was requested.")),
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    await TrySendAsync(
                        IpcEnvelope.Create(
                            MessageTypes.OperationFailed,
                            work.RequestId,
                            new OperationFailed(exception.Message)),
                        cancellationToken);
                }
                finally
                {
                    _operations.TryRemove(work.RequestId, out _);
                    work.Cancellation.Dispose();
                }
            }
        }

        private async Task SendHeartbeatsAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(heartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SendAsync(
                    IpcEnvelope.Create(MessageTypes.Heartbeat, null, new Heartbeat(DateTimeOffset.UtcNow)),
                    cancellationToken);
            }
        }

        private async ValueTask SendAsync(IpcEnvelope message, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await LengthPrefixedJson.WriteAsync(pipe, message, cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task TrySendAsync(IpcEnvelope message, CancellationToken cancellationToken)
        {
            try
            {
                await SendAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        private static async Task IgnoreShutdownExceptionAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        private sealed record WorkItem(
            Guid RequestId,
            JsonElement Arguments,
            IRequestHandlerInvoker Handler,
            CancellationTokenSource Cancellation);
    }

    private interface IRequestHandlerInvoker
    {
        string Contract { get; }
        Type RequestType { get; }
        ValueTask<JsonElement> InvokeAsync(
            JsonElement request,
            IpcRequestContext context,
            CancellationToken cancellationToken);
    }

    private sealed class RequestHandlerInvoker<TRequest, TResponse>(
        IIpcRequestHandler<TRequest, TResponse> handler) : IRequestHandlerInvoker
        where TRequest : IIpcRequest<TResponse>
    {
        public string Contract { get; } = ContractName.For<TRequest>();
        public Type RequestType => typeof(TRequest);

        public async ValueTask<JsonElement> InvokeAsync(
            JsonElement request,
            IpcRequestContext context,
            CancellationToken cancellationToken)
        {
            var typedRequest = request.Deserialize<TRequest>(IpcProtocol.JsonOptions)
                ?? throw new InvalidDataException($"Request contained no {typeof(TRequest).Name} payload.");
            var response = await handler.HandleAsync(typedRequest, context, cancellationToken);
            return JsonSerializer.SerializeToElement(response, IpcProtocol.JsonOptions);
        }
    }

    private sealed class DelegateRequestHandler<TRequest, TResponse>(
        Func<TRequest, IpcRequestContext, CancellationToken, ValueTask<TResponse>> handler)
        : IIpcRequestHandler<TRequest, TResponse>
        where TRequest : IIpcRequest<TResponse>
    {
        public ValueTask<TResponse> HandleAsync(
            TRequest request,
            IpcRequestContext context,
            CancellationToken cancellationToken) =>
            handler(request, context, cancellationToken);
    }
}
