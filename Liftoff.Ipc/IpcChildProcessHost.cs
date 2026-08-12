using System.Diagnostics;
using Liftoff.Ipc.Internal;

namespace Liftoff.Ipc;

/// <summary>
/// Owns an authenticated IPC server and the child process that connects to it.
/// </summary>
public sealed class IpcChildProcessHost : IDisposable
{
    private readonly Func<ProcessStartInfo> _createStartInfo;
    private readonly IpcChildProcessHostOptions _options;
    private readonly IChildProcessLauncher _processLauncher;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private IpcServer? _server;
    private IChildProcess? _childProcess;
    private string? _pipeName;
    private int _disposed;

    public IpcChildProcessHost(
        string executablePath,
        Action<IpcChildProcessHostOptions>? configure = null)
        : this(() => CreateStartInfo(executablePath), configure)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("The executable path cannot be empty.", nameof(executablePath));
        }
    }

    public IpcChildProcessHost(
        Func<string> getExecutablePath,
        Action<IpcChildProcessHostOptions>? configure = null)
        : this(CreateStartInfoFactory(getExecutablePath), configure)
    {
    }

    public IpcChildProcessHost(
        Func<ProcessStartInfo> createStartInfo,
        Action<IpcChildProcessHostOptions>? configure = null)
        : this(createStartInfo, CreateOptions(configure), new ChildProcessLauncher())
    {
    }

    internal IpcChildProcessHost(
        Func<ProcessStartInfo> createStartInfo,
        IpcChildProcessHostOptions options,
        IChildProcessLauncher processLauncher)
    {
        _createStartInfo = createStartInfo
            ?? throw new ArgumentNullException(nameof(createStartInfo));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processLauncher = processLauncher
            ?? throw new ArgumentNullException(nameof(processLauncher));
        ValidateOptions(options);
    }

    public event EventHandler<IpcChildProcessExitedEventArgs>? ChildProcessExited;

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _server is not null;
            }
        }
    }

    public bool IsChildProcessRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _childProcess is not null;
            }
        }
    }

    public int? ChildProcessId
    {
        get
        {
            lock (_stateGate)
            {
                return _childProcess?.Id;
            }
        }
    }

    public string? PipeName
    {
        get
        {
            lock (_stateGate)
            {
                return _pipeName;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        StartAsync(_ => { }, cancellationToken);

    public async Task StartAsync(
        Action<IpcServer> configureServer,
        CancellationToken cancellationToken = default)
    {
        if (configureServer is null)
        {
            throw new ArgumentNullException(nameof(configureServer));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsRunning)
            {
                throw new InvalidOperationException(
                    "The child process host is already running. Use RestartAsync to replace it.");
            }

            await StartCoreAsync(configureServer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task RestartAsync(CancellationToken cancellationToken = default) =>
        RestartAsync(_ => { }, cancellationToken);

    public async Task RestartAsync(
        Action<IpcServer> configureServer,
        CancellationToken cancellationToken = default)
    {
        if (configureServer is null)
        {
            throw new ArgumentNullException(nameof(configureServer));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(configureServer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<int> PublishAsync<TEvent>(
        TEvent data,
        CancellationToken cancellationToken = default)
        where TEvent : IIpcEvent
    {
        IpcServer server;
        lock (_stateGate)
        {
            server = _server
                ?? throw new InvalidOperationException("The child process host is not running.");
        }

        return server.PublishAsync(data, cancellationToken);
    }

    public async Task PublishBestEffortAsync<TEvent>(
        TEvent data,
        CancellationToken cancellationToken = default)
        where TEvent : IIpcEvent
    {
        IpcServer? server;
        lock (_stateGate)
        {
            server = _server;
        }

        if (server is null)
        {
            return;
        }

        try
        {
            await server.PublishAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (IpcDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();

    private async Task StartCoreAsync(
        Action<IpcServer> configureServer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = IpcSession.Create(_options.PipeName);
        var server = IpcServer.Create(session, _options.ConfigureServer);
        IChildProcess? childProcess = null;
        try
        {
            configureServer(server);
            await server.StartAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = _createStartInfo()
                ?? throw new IpcConfigurationException(
                    "The child process start information factory returned null.");
            ValidateStartInfo(startInfo);
            session.ConfigureChildProcess(startInfo);

            childProcess = _processLauncher.Start(startInfo);
            childProcess.Exited += OnChildProcessExited;
            lock (_stateGate)
            {
                _server = server;
                _childProcess = childProcess;
                _pipeName = session.PipeName;
            }
            childProcess.EnableRaisingEvents = true;
        }
        catch
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_server, server))
                {
                    _server = null;
                    _pipeName = null;
                }

                if (ReferenceEquals(_childProcess, childProcess))
                {
                    _childProcess = null;
                }
            }

            try
            {
                if (childProcess is not null)
                {
                    childProcess.Exited -= OnChildProcessExited;
                    await StopChildProcessAsync(childProcess, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        IpcServer? server;
        IChildProcess? childProcess;
        lock (_stateGate)
        {
            server = _server;
            childProcess = _childProcess;
            _server = null;
            _childProcess = null;
            _pipeName = null;
            if (childProcess is not null)
            {
                childProcess.Exited -= OnChildProcessExited;
            }
        }

        Exception? processError = null;
        if (childProcess is not null)
        {
            try
            {
                await StopChildProcessAsync(childProcess, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                processError = exception;
            }
        }

        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }

        if (processError is not null)
        {
            throw processError;
        }
    }

    private async Task StopChildProcessAsync(
        IChildProcess childProcess,
        CancellationToken cancellationToken)
    {
        try
        {
            if (childProcess.HasExited)
            {
                return;
            }

            childProcess.CloseMainWindow();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ShutdownTimeout);
            try
            {
                await childProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                if (!childProcess.HasExited)
                {
                    childProcess.Kill(_options.KillEntireProcessTree);
                    await childProcess.WaitForExitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            childProcess.Dispose();
        }
    }

    private void OnChildProcessExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not IChildProcess exitedProcess)
        {
            return;
        }

        IChildProcess? childProcess;
        lock (_stateGate)
        {
            childProcess = _childProcess;
            if (childProcess is null || !ReferenceEquals(childProcess, exitedProcess))
            {
                return;
            }

            _childProcess = null;
            childProcess.Exited -= OnChildProcessExited;
        }

        try
        {
            var processId = childProcess.Id;
            var exitCode = childProcess.ExitCode;
            ChildProcessExited?.Invoke(
                this,
                new IpcChildProcessExitedEventArgs(processId, exitCode));
        }
        finally
        {
            childProcess.Dispose();
        }
    }

    private void ValidateStartInfo(ProcessStartInfo startInfo)
    {
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
        {
            throw new IpcConfigurationException(
                "The child process executable path cannot be empty.");
        }

        var executablePath = startInfo.FileName;
        if (!Path.IsPathRooted(executablePath)
            && !string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
        {
            executablePath = Path.Combine(startInfo.WorkingDirectory, executablePath);
        }

        if (_options.ValidateExecutableExists && !File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "The child process executable could not be found.",
                executablePath);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath);
        var directory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            startInfo.WorkingDirectory = directory;
        }

        return startInfo;
    }

    private static Func<ProcessStartInfo> CreateStartInfoFactory(
        Func<string> getExecutablePath)
    {
        if (getExecutablePath is null)
        {
            throw new ArgumentNullException(nameof(getExecutablePath));
        }

        return () => CreateStartInfo(getExecutablePath());
    }

    private static IpcChildProcessHostOptions CreateOptions(
        Action<IpcChildProcessHostOptions>? configure)
    {
        var options = new IpcChildProcessHostOptions();
        configure?.Invoke(options);
        ValidateOptions(options);
        return options;
    }

    private static void ValidateOptions(IpcChildProcessHostOptions options)
    {
        if (options.PipeName is not null && string.IsNullOrWhiteSpace(options.PipeName))
        {
            throw new ArgumentException("The pipe name cannot be empty.", nameof(options.PipeName));
        }

        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ShutdownTimeout),
                "The shutdown timeout must be greater than zero.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(IpcChildProcessHost));
        }
    }
}
