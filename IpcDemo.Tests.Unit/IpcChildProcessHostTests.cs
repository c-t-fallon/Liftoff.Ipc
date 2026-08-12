using System.Diagnostics;
using Liftoff.Ipc.Internal;

namespace Liftoff.Ipc.Tests;

public sealed class IpcChildProcessHostTests
{
    [Fact]
    public async Task Start_configures_an_authenticated_child_process()
    {
        var process = new FakeChildProcess(exitWhenCloseRequested: true);
        var launcher = new FakeChildProcessLauncher(process);
        await using var host = CreateHost(launcher);

        await host.StartAsync();

        var startInfo = Assert.IsType<ProcessStartInfo>(launcher.StartInfos.Single());
        Assert.True(host.IsRunning);
        Assert.True(host.IsChildProcessRunning);
        Assert.Equal(process.Id, host.ChildProcessId);
        Assert.Equal(host.PipeName, startInfo.EnvironmentVariables[
            IpcSession.PipeNameEnvironmentVariable]);
        Assert.NotNull(startInfo.EnvironmentVariables[
            IpcSession.AuthenticationKeyEnvironmentVariable]);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public async Task Start_rejects_an_already_running_host()
    {
        var launcher = new FakeChildProcessLauncher(
            new FakeChildProcess(exitWhenCloseRequested: true));
        await using var host = CreateHost(launcher);
        await host.StartAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync());

        Assert.Contains("RestartAsync", exception.Message);
        Assert.Single(launcher.StartInfos);
    }

    [Fact]
    public async Task Restart_stops_the_previous_child_and_starts_a_new_session()
    {
        var first = new FakeChildProcess(exitWhenCloseRequested: true);
        var second = new FakeChildProcess(exitWhenCloseRequested: true);
        var launcher = new FakeChildProcessLauncher(first, second);
        await using var host = CreateHost(launcher);
        await host.StartAsync();
        var firstPipeName = host.PipeName;

        await host.RestartAsync();

        Assert.True(first.CloseRequested);
        Assert.True(first.Disposed);
        Assert.Equal(second.Id, host.ChildProcessId);
        Assert.NotEqual(firstPipeName, host.PipeName);
        Assert.Equal(2, launcher.StartInfos.Count);
    }

    [Fact]
    public async Task Launch_failure_rolls_back_and_allows_another_start()
    {
        var process = new FakeChildProcess(exitWhenCloseRequested: true);
        var launcher = new FakeChildProcessLauncher(
            new InvalidOperationException("launch failed"),
            process);
        await using var host = CreateHost(launcher);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync());
        await host.StartAsync();

        Assert.Equal("launch failed", exception.Message);
        Assert.True(host.IsRunning);
        Assert.Equal(process.Id, host.ChildProcessId);
    }

    [Fact]
    public async Task Stop_forces_a_child_that_does_not_exit_gracefully()
    {
        var process = new FakeChildProcess(exitWhenCloseRequested: false);
        var launcher = new FakeChildProcessLauncher(process);
        await using var host = CreateHost(
            launcher,
            options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(10));
        await host.StartAsync();

        await host.StopAsync();

        Assert.True(process.CloseRequested);
        Assert.True(process.KillRequested);
        Assert.True(process.KillEntireProcessTree);
        Assert.True(process.Disposed);
        Assert.False(host.IsRunning);
        Assert.False(host.IsChildProcessRunning);
    }

    [Fact]
    public async Task Unexpected_child_exit_updates_state_and_raises_an_event()
    {
        var process = new FakeChildProcess(exitWhenCloseRequested: false);
        var launcher = new FakeChildProcessLauncher(process);
        await using var host = CreateHost(launcher);
        IpcChildProcessExitedEventArgs? exited = null;
        host.ChildProcessExited += (_, eventArgs) => exited = eventArgs;
        await host.StartAsync();

        process.Exit(23);

        Assert.False(host.IsChildProcessRunning);
        Assert.True(host.IsRunning);
        Assert.NotNull(exited);
        Assert.Equal(process.Id, exited.ProcessId);
        Assert.Equal(23, exited.ExitCode);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Publishing_is_explicit_when_stopped_and_best_effort_is_a_no_op()
    {
        var launcher = new FakeChildProcessLauncher(
            new FakeChildProcess(exitWhenCloseRequested: true));
        await using var host = CreateHost(launcher);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.PublishAsync(new TestEvent()));
        await host.PublishBestEffortAsync(new TestEvent());
    }

    private static IpcChildProcessHost CreateHost(
        IChildProcessLauncher launcher,
        Action<IpcChildProcessHostOptions>? configure = null)
    {
        var options = new IpcChildProcessHostOptions
        {
            ValidateExecutableExists = false
        };
        configure?.Invoke(options);
        return new IpcChildProcessHost(
            () => new ProcessStartInfo("fake-child.exe"),
            options,
            launcher);
    }

    private sealed record TestEvent : IIpcEvent;

    private sealed class FakeChildProcessLauncher : IChildProcessLauncher
    {
        private readonly Queue<object> _results;

        public FakeChildProcessLauncher(params object[] results)
        {
            _results = new Queue<object>(results);
        }

        public List<ProcessStartInfo> StartInfos { get; } = new();

        public IChildProcess Start(ProcessStartInfo startInfo)
        {
            StartInfos.Add(startInfo);
            var result = _results.Dequeue();
            return result is Exception exception
                ? throw exception
                : (IChildProcess)result;
        }
    }

    private sealed class FakeChildProcess : IChildProcess
    {
        private static int _nextId = 100;
        private readonly bool _exitWhenCloseRequested;
        private readonly TaskCompletionSource<bool> _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeChildProcess(bool exitWhenCloseRequested)
        {
            _exitWhenCloseRequested = exitWhenCloseRequested;
            Id = Interlocked.Increment(ref _nextId);
        }

        public event EventHandler? Exited;
        public int Id { get; }
        public int ExitCode { get; private set; }
        public bool HasExited { get; private set; }
        public bool EnableRaisingEvents { private get; set; }
        public bool CloseRequested { get; private set; }
        public bool KillRequested { get; private set; }
        public bool KillEntireProcessTree { get; private set; }
        public bool Disposed { get; private set; }

        public bool CloseMainWindow()
        {
            CloseRequested = true;
            if (_exitWhenCloseRequested)
            {
                Exit(0);
            }
            return _exitWhenCloseRequested;
        }

        public void Kill(bool entireProcessTree)
        {
            KillRequested = true;
            KillEntireProcessTree = entireProcessTree;
            Exit(-1);
        }

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            await AsyncCompatibility.WaitWithCancellationAsync(_exit.Task, cancellationToken);
        }

        public void Exit(int exitCode)
        {
            if (HasExited)
            {
                return;
            }

            ExitCode = exitCode;
            HasExited = true;
            _exit.TrySetResult(true);
            if (EnableRaisingEvents)
            {
                Exited?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Dispose() => Disposed = true;
    }
}
