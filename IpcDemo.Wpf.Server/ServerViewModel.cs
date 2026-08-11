using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IpcDemo.Contracts;
using IpcDemo.Wpf.Shared;
using Liftoff.Ipc;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace IpcDemo.Wpf.Server;

public partial class ServerViewModel : ObservableObject, IAsyncDisposable
{
    private IpcServer? _server;
    private Process? _childProcess;
    private CancellationTokenSource? _publisherLifetime;
    private Task? _publisherTask;
    private readonly object _themeStateGate = new();
    private DateTimeOffset _themeChangedAt = DateTimeOffset.UtcNow;

    public ServerViewModel()
    {
        Theme = new ThemeService("IpcDemo.Wpf.Server");
        Theme.PropertyChanged += OnThemePropertyChanged;
        Activity.Add(new(DateTimeOffset.Now, "READY", "Station ready", "Start listening to accept client connections."));
    }

    public ThemeService Theme { get; }
    public ObservableCollection<TimelineEntry> Activity { get; } = [];
    [ObservableProperty] private string pipeName = DemoIpc.PipeName;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isClientRunning;
    [ObservableProperty] private bool publishEvents = true;
    [ObservableProperty] private string stationLabel = "STOPPED";
    [ObservableProperty] private int requestCount;
    [ObservableProperty] private int eventCount;
    [ObservableProperty] private int deliveredCount;
    [ObservableProperty] private string currentWork = "No active request";
    [ObservableProperty] private double currentProgress;

    private bool CanStart => !IsRunning && !string.IsNullOrWhiteSpace(PipeName);
    private bool CanStartAndLaunchClient => CanStart && !IsClientRunning;
    private bool CanStop => IsRunning;
    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StartAndLaunchClientCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsClientRunningChanged(bool value) => StartAndLaunchClientCommand.NotifyCanExecuteChanged();
    partial void OnPipeNameChanged(string value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StartAndLaunchClientCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => StartCoreAsync(session: null);

    [RelayCommand(CanExecute = nameof(CanStartAndLaunchClient))]
    private async Task StartAndLaunchClientAsync()
    {
        var session = IpcSession.Create(PipeName);
        if (!await StartCoreAsync(session))
        {
            return;
        }

        try
        {
            var clientPath = FindClientExecutable();
            var startInfo = new ProcessStartInfo(clientPath)
            {
                WorkingDirectory = Path.GetDirectoryName(clientPath)!
            };
            session.ConfigureChildProcess(startInfo);

            _childProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The client process could not be started.");
            _childProcess.Exited += OnChildProcessExited;
            _childProcess.EnableRaisingEvents = true;
            IsClientRunning = true;
            Add("LAUNCH", "Client process started", $"PID {_childProcess.Id} · authenticated session {session.PipeName}");
        }
        catch (Exception exception)
        {
            Add("ERROR", "Client failed to launch", exception.Message);
            await StopCoreAsync();
        }
    }

    private async Task<bool> StartCoreAsync(IpcSession? session)
    {
        try
        {
            _server = session is null
                ? IpcServer.Create(PipeName)
                : IpcServer.Create(session);
            _server.RegisterHandler<AnalyzeModelRequest, AnalyzeModelResult>(HandleRequestAsync);
            _server.RegisterHandler<GetThemeStateRequest, ThemeState>((_, _, _) =>
                Task.FromResult(GetThemeState()));
            await _server.StartAsync();
            _publisherLifetime = new CancellationTokenSource();
            _publisherTask = PublishEventsAsync(_publisherLifetime.Token);
            IsRunning = true;
            StationLabel = "LISTENING";
            Add("LISTEN", "Named pipe opened", PipeName);
            return true;
        }
        catch (Exception exception)
        {
            StationLabel = "FAULTED";
            Add("ERROR", "Server failed to start", exception.Message);
            if (_server is not null) await _server.DisposeAsync();
            _server = null;
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync() => await StopCoreAsync();

    [RelayCommand]
    private void ClearActivity() => Activity.Clear();

    private async Task<AnalyzeModelResult> HandleRequestAsync(AnalyzeModelRequest request, IpcRequestContext context, CancellationToken token)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            RequestCount++;
            CurrentWork = request.ModelName;
            CurrentProgress = 0;
        });
        Add("REQUEST", "AnalyzeModelRequest accepted", $"{request.ModelName} · {request.Steps} batches");
        if (request.Steps is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(request.Steps), "Steps must be between 1 and 100.");
        var timer = Stopwatch.StartNew();
        try
        {
            for (var step = 1; step <= request.Steps; step++)
            {
                await Task.Delay(request.DelayMilliseconds, token);
                var percent = step * 100d / request.Steps;
                await context.ReportProgressAsync(percent, $"Analyzed batch {step} of {request.Steps}.", token);
                await Application.Current.Dispatcher.InvokeAsync(() => { CurrentProgress = percent; Add("PROGRESS", $"Batch {step}/{request.Steps}", $"{percent:0}% reported to client."); });
            }
            if (request.ShouldFail) throw new InvalidOperationException("The demo operation was asked to fail.");
            var result = new AnalyzeModelResult(request.ModelName, request.Steps * 125, timer.Elapsed);
            Add("RESPONSE", "AnalyzeModelResult sent", $"{result.ElementsAnalyzed:N0} elements in {result.Elapsed.TotalSeconds:0.00}s");
            return result;
        }
        catch (OperationCanceledException)
        {
            Add("CANCEL", "Request cancelled", $"Work on {request.ModelName} stopped early.");
            throw;
        }
        catch (Exception exception)
        {
            Add("ERROR", "Handler failed", exception.Message);
            throw;
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { CurrentWork = "No active request"; CurrentProgress = 0; });
        }
    }

    private async Task PublishEventsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                if (!PublishEvents || _server is null) continue;
                var sequence = ++EventCount;
                var delivered = await _server.PublishAsync(new ModelChanged(sequence, $"Element-{sequence}", DateTimeOffset.UtcNow), token);
                DeliveredCount += delivered;
                if (delivered > 0) Add("EVENT", $"Element-{sequence}", $"Delivered to {delivered} subscription(s).");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async void OnThemePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(ThemeService.IsDark) || _server is null || !IsRunning)
        {
            return;
        }

        try
        {
            ThemeState state;
            lock (_themeStateGate)
            {
                _themeChangedAt = DateTimeOffset.UtcNow;
                state = new ThemeState(Theme.IsDark, _themeChangedAt);
            }
            EventCount++;
            var delivered = await _server.PublishAsync(
                new ThemeChanged(state.IsDark, state.ChangedAt));
            DeliveredCount += delivered;
            Add("THEME", Theme.IsDark ? "Dark theme published" : "Light theme published",
                $"Delivered to {delivered} synchronized client(s).");
        }
        catch (Exception exception)
        {
            Add("ERROR", "Theme could not be published", exception.Message);
        }
    }

    private ThemeState GetThemeState()
    {
        lock (_themeStateGate)
        {
            return new ThemeState(Theme.IsDark, _themeChangedAt);
        }
    }

    private void Add(string kind, string title, string detail)
    {
        void Insert() { Activity.Insert(0, new(DateTimeOffset.Now, kind, title, detail)); while (Activity.Count > 100) Activity.RemoveAt(Activity.Count - 1); }
        if (Application.Current.Dispatcher.CheckAccess()) Insert(); else Application.Current.Dispatcher.Invoke(Insert);
    }

    private async Task StopCoreAsync()
    {
        await StopChildProcessAsync();
        _publisherLifetime?.Cancel();
        if (_publisherTask is not null) await _publisherTask;
        if (_server is not null) await _server.DisposeAsync();
        _publisherLifetime?.Dispose(); _publisherLifetime = null; _publisherTask = null; _server = null;
        IsRunning = false; StationLabel = "STOPPED"; CurrentWork = "No active request"; CurrentProgress = 0;
        Add("STOP", "Server stopped", "The pipe and active sessions were disposed.");
    }

    private static string FindClientExecutable()
    {
        var fileName = "IpcDemo.Wpf.Client.exe";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Client", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        ];

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                $"Could not find {fileName}. Build the server project so its Client output folder is populated.");
    }

    private async Task StopChildProcessAsync()
    {
        var process = _childProcess;
        if (process is null)
        {
            return;
        }

        _childProcess = null;
        IsClientRunning = false;
        process.Exited -= OnChildProcessExited;

        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            Add("CHILD", "Client process stopped", "The child client was closed with the server.");
        }
        catch (Exception exception)
        {
            Add("ERROR", "Client process could not be stopped", exception.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnChildProcessExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_childProcess, process))
            {
                return;
            }

            _childProcess = null;
            IsClientRunning = false;
            process.Exited -= OnChildProcessExited;
            Add("CHILD", "Client process exited", $"PID {process.Id} closed independently.");
            process.Dispose();
        });
    }

    public async ValueTask DisposeAsync()
    {
        Theme.PropertyChanged -= OnThemePropertyChanged;
        await StopCoreAsync();
    }
}
