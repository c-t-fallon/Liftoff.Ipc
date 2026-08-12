using System.Diagnostics;

namespace Liftoff.Ipc.Internal;

internal interface IChildProcessLauncher
{
    IChildProcess Start(ProcessStartInfo startInfo);
}

internal interface IChildProcess : IDisposable
{
    event EventHandler? Exited;
    int Id { get; }
    int ExitCode { get; }
    bool HasExited { get; }
    bool EnableRaisingEvents { set; }
    bool CloseMainWindow();
    void Kill(bool entireProcessTree);
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

internal sealed class ChildProcessLauncher : IChildProcessLauncher
{
    public IChildProcess Start(ProcessStartInfo startInfo) =>
        new ChildProcess(
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("The child process could not be started."));
}

internal sealed class ChildProcess : IChildProcess
{
    private readonly Process _process;

    public ChildProcess(Process process)
    {
        _process = process;
        _process.Exited += OnExited;
    }

    public event EventHandler? Exited;

    public int Id => _process.Id;
    public int ExitCode => _process.ExitCode;
    public bool HasExited => _process.HasExited;
    public bool EnableRaisingEvents { set => _process.EnableRaisingEvents = value; }
    public bool CloseMainWindow() => _process.CloseMainWindow();

    public void Kill(bool entireProcessTree)
    {
#if NETFRAMEWORK
        _process.Kill();
#else
        _process.Kill(entireProcessTree);
#endif
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
#if NETFRAMEWORK
        if (_process.HasExited)
        {
            return;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => completion.TrySetResult(true);
        _process.Exited += handler;
        _process.EnableRaisingEvents = true;
        try
        {
            if (_process.HasExited)
            {
                return;
            }

            await AsyncCompatibility.WaitWithCancellationAsync(
                    completion.Task,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _process.Exited -= handler;
        }
#else
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#endif
    }

    public void Dispose()
    {
        _process.Exited -= OnExited;
        _process.Dispose();
    }

    private void OnExited(object? sender, EventArgs eventArgs) =>
        Exited?.Invoke(this, eventArgs);
}
