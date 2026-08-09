using System.IO.Pipes;
using System.Runtime.CompilerServices;

namespace Liftoff.Ipc.Internal;

internal interface IIpcTransport : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(IpcEnvelope message, CancellationToken cancellationToken = default);
    IAsyncEnumerable<IpcEnvelope> ReadAllAsync(CancellationToken cancellationToken = default);
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
            await pipe.ConnectAsync(cancellationToken);
            if (authenticationKey is not null)
            {
                await IpcAuthenticator.AuthenticateClientAsync(
                    pipe,
                    authenticationKey,
                    options.AuthenticationTimeout,
                    cancellationToken);
            }

            _pipe = pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(IpcEnvelope message, CancellationToken cancellationToken = default)
    {
        var pipe = GetConnectedPipe();
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

    public async IAsyncEnumerable<IpcEnvelope> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pipe = GetConnectedPipe();
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await LengthPrefixedJson.ReadAsync(pipe, cancellationToken);
            if (message is null)
            {
                yield break;
            }

            yield return message;
        }
    }

    public ValueTask DisposeAsync()
    {
        _pipe?.Dispose();
        _writeGate.Dispose();
        return default;
    }

    private NamedPipeClientStream GetConnectedPipe() =>
        _pipe is { IsConnected: true } pipe
            ? pipe
            : throw new IpcDisconnectedException("The named-pipe transport is not connected.");
}
