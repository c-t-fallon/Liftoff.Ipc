namespace Liftoff.Ipc;

public interface IIpcRequest<TResponse>;

public interface IIpcEvent;

public interface IIpcRequestHandler<in TRequest, TResponse>
    where TRequest : IIpcRequest<TResponse>
{
    ValueTask<TResponse> HandleAsync(
        TRequest request,
        IpcRequestContext context,
        CancellationToken cancellationToken);
}

public interface IIpcClient : IAsyncDisposable
{
    Task<TResponse> RequestAsync<TResponse>(
        IIpcRequest<TResponse> request,
        IProgress<IpcProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IpcSubscription<TEvent>> SubscribeAsync<TEvent>(
        CancellationToken cancellationToken = default)
        where TEvent : IIpcEvent;
}

public sealed record IpcProgress(double Percent, string Message);

public readonly record struct IpcUnit;

public interface IIpcCommand : IIpcRequest<IpcUnit>;

public sealed class IpcRequestContext
{
    private readonly Func<IpcProgress, CancellationToken, ValueTask> _reportProgress;

    internal IpcRequestContext(Func<IpcProgress, CancellationToken, ValueTask> reportProgress)
    {
        _reportProgress = reportProgress;
    }

    public ValueTask ReportProgressAsync(
        double percent,
        string message,
        CancellationToken cancellationToken = default) =>
        _reportProgress(new IpcProgress(percent, message), cancellationToken);
}

public sealed class IpcClientOptions
{
    public TimeSpan AcknowledgementTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

public sealed class IpcServerOptions
{
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);
}

public class IpcException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class IpcRemoteException(string message) : IpcException(message);

public sealed class IpcDisconnectedException : IpcException
{
    public IpcDisconnectedException(string message) : base(message) { }
    public IpcDisconnectedException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class IpcConfigurationException(string message) : IpcException(message);
