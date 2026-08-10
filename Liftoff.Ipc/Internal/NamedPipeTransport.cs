using System.IO.Pipes;
namespace Liftoff.Ipc.Internal;

internal interface IIpcTransport : IDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(IpcEnvelope message, CancellationToken cancellationToken = default);
    Task<IpcEnvelope?> ReadAsync(CancellationToken cancellationToken = default);
    Task DisposeAsync();
}

internal sealed class NamedPipeTransport(
    string pipeName,
    IpcClientOptions options,
    byte[]? authenticationKey = null) : IIpcTransport
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NamedPipeClientStream? _pipe;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        var pipeOptions = PipeOptions.Asynchronous;
#if !NETFRAMEWORK
        if (options.CurrentUserOnly)
        {
            pipeOptions |= PipeOptions.CurrentUserOnly;
        }
#endif

        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            pipeOptions);

        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (authenticationKey is not null)
            {
                await IpcAuthenticator.AuthenticateClientAsync(
                    pipe,
                    authenticationKey,
                    options.AuthenticationTimeout,
                    cancellationToken).ConfigureAwait(false);
            }

            _pipe = pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public async Task SendAsync(IpcEnvelope message, CancellationToken cancellationToken = default)
    {
        var pipe = GetConnectedPipe();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProtocolFraming.WriteAsync(pipe, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<IpcEnvelope?> ReadAsync(CancellationToken cancellationToken = default) =>
        ProtocolFraming.ReadAsync(GetConnectedPipe(), cancellationToken);

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _writeGate.Dispose();
    }

    private NamedPipeClientStream GetConnectedPipe() =>
        _pipe is { IsConnected: true } pipe
            ? pipe
            : throw new IpcDisconnectedException("The named-pipe transport is not connected.");
}
