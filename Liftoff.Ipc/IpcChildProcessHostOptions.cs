namespace Liftoff.Ipc;

/// <summary>
/// Configures the standard lifecycle policy used by <see cref="IpcChildProcessHost"/>.
/// </summary>
public sealed class IpcChildProcessHostOptions
{
    /// <summary>
    /// Gets or sets the pipe name for the session. The default creates a unique name.
    /// </summary>
    public string? PipeName { get; set; }

    /// <summary>
    /// Gets or sets the time allowed for the child to exit after its main window is asked to close.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets whether forced shutdown terminates the child's descendant processes too.
    /// On .NET Framework, where process-tree termination is unavailable, only the child is terminated.
    /// </summary>
    public bool KillEntireProcessTree { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the executable named by the generated start information must exist as a file.
    /// Disable this only when relying on operating-system executable lookup.
    /// </summary>
    public bool ValidateExecutableExists { get; set; } = true;

    /// <summary>
    /// Gets or sets additional configuration applied when the host creates its IPC server.
    /// </summary>
    public Action<IpcServerOptions>? ConfigureServer { get; set; }
}

public sealed class IpcChildProcessExitedEventArgs : EventArgs
{
    internal IpcChildProcessExitedEventArgs(int processId, int exitCode)
    {
        ProcessId = processId;
        ExitCode = exitCode;
    }

    public int ProcessId { get; }
    public int ExitCode { get; }
}
