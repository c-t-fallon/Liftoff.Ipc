using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IpcDemo.Contracts;
using IpcDemo.Wpf.Shared;
using Liftoff.Ipc;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace IpcDemo.Wpf.Client;

public partial class ClientViewModel : ObservableObject, IAsyncDisposable
{
    private IpcClient? _client;
    private IpcSession? _parentSession;
    private IpcSubscription<ModelChanged>? _subscription;
    private IpcSubscription<ThemeChanged>? _themeSubscription;
    private CancellationTokenSource? _requestLifetime;
    private CancellationTokenSource? _eventLifetime;
    private Task? _eventReader;
    private CancellationTokenSource? _themeLifetime;
    private Task? _themeReader;
    private DateTimeOffset _lastThemeChangedAt = DateTimeOffset.MinValue;
    private readonly DispatcherTimer _heartbeatTimer;

    public ClientViewModel()
    {
        Theme = new ThemeService("IpcDemo.Wpf.Client");
        Activity.Add(new(DateTimeOffset.Now, "READY", "Workbench ready", "Start the server, then connect to IpcDemo.Pipe."));
        try
        {
            if (IpcSession.TryFromEnvironment(out var session))
            {
                _parentSession = session;
                PipeName = session!.PipeName;
                Activity.Add(new(DateTimeOffset.Now, "PARENT", "Parent session detected", "The client will connect automatically when the window opens."));
            }
        }
        catch (IpcConfigurationException exception)
        {
            Activity.Add(new(DateTimeOffset.Now, "ERROR", "Parent session is invalid", exception.Message));
        }
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heartbeatTimer.Tick += (_, _) => RefreshHeartbeat();
        _heartbeatTimer.Start();
    }

    public ThemeService Theme { get; }
    public ObservableCollection<TimelineEntry> Activity { get; } = [];
    public ObservableCollection<ModelChanged> Events { get; } = [];
    public bool ShouldAutoConnect => _parentSession is not null;

    [ObservableProperty] private string pipeName = DemoIpc.PipeName;
    [ObservableProperty] private string modelName = "Learning Model";
    [ObservableProperty] private int steps = 8;
    [ObservableProperty] private int delayMilliseconds = 400;
    [ObservableProperty] private bool shouldFail;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isSubscribed;
    [ObservableProperty] private double progressPercent;
    [ObservableProperty] private string progressMessage = "No request in flight";
    [ObservableProperty] private string connectionLabel = "OFFLINE";
    [ObservableProperty] private string heartbeatLabel = "No heartbeat";
    [ObservableProperty] private string resultHeadline = "—";
    [ObservableProperty] private string resultDetail = "Submit a request to see the response.";

    private bool CanConnect => !IsConnected && !IsBusy && !string.IsNullOrWhiteSpace(PipeName);
    private bool CanDisconnect => IsConnected && !IsBusy;
    private bool CanSend => IsConnected && !IsBusy && Steps is >= 1 and <= 100 && DelayMilliseconds >= 0;
    private bool CanCancel => IsBusy;
    private bool CanToggleSubscription => IsConnected;

    partial void OnIsConnectedChanged(bool value) { ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged(); SendCommand.NotifyCanExecuteChanged(); ToggleSubscriptionCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) { ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged(); SendCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); }
    partial void OnPipeNameChanged(string value) => ConnectCommand.NotifyCanExecuteChanged();
    partial void OnStepsChanged(int value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnDelayMillisecondsChanged(int value) => SendCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        ConnectionLabel = "CONNECTING";
        Add("CONNECT", "Opening named pipe", PipeName);
        try
        {
            _client = _parentSession is null
                ? await IpcClient.ConnectAsync(PipeName)
                : await IpcClient.ConnectAsync(_parentSession);
            await StartThemeSyncAsync(_client);
            IsConnected = true;
            ConnectionLabel = "AUTHENTICATED";
            Add("ACCEPT", "Connection authenticated", "The transport reader and heartbeat monitor are active.");
        }
        catch (Exception exception)
        {
            await DisconnectCoreAsync();
            ConnectionLabel = "OFFLINE";
            Add("ERROR", "Connection failed", exception.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync() => await DisconnectCoreAsync();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (_client is null) return;
        IsBusy = true;
        ProgressPercent = 0;
        ProgressMessage = "Sending request envelope";
        ResultHeadline = "Working…";
        ResultDetail = $"{Steps} batches × {DelayMilliseconds} ms";
        _requestLifetime = new CancellationTokenSource();
        Add("SEND", "AnalyzeModelRequest", $"Model={ModelName}; Steps={Steps}; ShouldFail={ShouldFail}");
        var progress = new Progress<IpcProgress>(p =>
        {
            ProgressPercent = p.Percent;
            ProgressMessage = p.Message;
            Add("PROGRESS", $"{p.Percent:0}%", p.Message);
        });

        try
        {
            var result = await _client.RequestAsync(new AnalyzeModelRequest(ModelName, Steps, DelayMilliseconds, ShouldFail), progress, _requestLifetime.Token);
            ProgressPercent = 100;
            ProgressMessage = "Response received";
            ResultHeadline = $"{result.ElementsAnalyzed:N0} elements";
            ResultDetail = $"Analyzed {result.ModelName} in {result.Elapsed.TotalSeconds:0.00} seconds.";
            Add("DONE", "AnalyzeModelResult", ResultDetail);
        }
        catch (OperationCanceledException)
        {
            ProgressMessage = "Cancellation confirmed";
            ResultHeadline = "Cancelled";
            ResultDetail = "The client sent a cancellation frame to the server.";
            Add("CANCEL", "Request cancelled", ResultDetail);
        }
        catch (Exception exception)
        {
            ProgressMessage = "Remote operation failed";
            ResultHeadline = "Failed";
            ResultDetail = exception.Message;
            Add("ERROR", "Request failed", exception.Message);
        }
        finally
        {
            _requestLifetime.Dispose();
            _requestLifetime = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _requestLifetime?.Cancel();

    [RelayCommand(CanExecute = nameof(CanToggleSubscription))]
    private async Task ToggleSubscriptionAsync()
    {
        if (_client is null) return;
        if (_subscription is not null)
        {
            _eventLifetime?.Cancel();
            if (_eventReader is not null) await _eventReader;
            await _subscription.DisposeAsync();
            _subscription = null;
            _eventLifetime?.Dispose();
            _eventLifetime = null;
            IsSubscribed = false;
            Add("EVENT", "Subscription closed", "ModelChanged events will no longer be delivered.");
            return;
        }

        try
        {
            _subscription = await _client.SubscribeAsync<ModelChanged>();
            _eventLifetime = new CancellationTokenSource();
            _eventReader = ReadEventsAsync(_subscription, _eventLifetime.Token);
            IsSubscribed = true;
            Add("EVENT", "Subscription accepted", "Listening for ModelChanged events.");
        }
        catch (Exception exception) { Add("ERROR", "Subscription failed", exception.Message); }
    }

    [RelayCommand]
    private void ClearActivity() { Activity.Clear(); Events.Clear(); }

    private async Task ReadEventsAsync(IpcSubscription<ModelChanged> subscription, CancellationToken token)
    {
        try
        {
            await foreach (var item in subscription.WithCancellation(token))
            {
                Events.Insert(0, item);
                while (Events.Count > 20) Events.RemoveAt(Events.Count - 1);
                Add("EVENT", item.ElementName, $"Sequence {item.Sequence} published by the server.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Add("ERROR", "Event stream ended", exception.Message); }
    }

    private async Task StartThemeSyncAsync(IpcClient client)
    {
        _themeSubscription = await client.SubscribeAsync<ThemeChanged>();
        _themeLifetime = new CancellationTokenSource();
        _themeReader = ReadThemeEventsAsync(_themeSubscription, _themeLifetime.Token);

        var state = await client.RequestAsync(new GetThemeStateRequest());
        await ApplyServerThemeAsync(state.IsDark, state.ChangedAt, "Initial theme state received");
    }

    private async Task ReadThemeEventsAsync(IpcSubscription<ThemeChanged> subscription, CancellationToken token)
    {
        try
        {
            await foreach (var update in subscription.WithCancellation(token))
            {
                await ApplyServerThemeAsync(update.IsDark, update.ChangedAt, "ThemeChanged event received");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => Add("ERROR", "Theme sync ended", exception.Message));
        }
    }

    private async Task ApplyServerThemeAsync(bool isDark, DateTimeOffset changedAt, string source)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (changedAt < _lastThemeChangedAt) return;
            _lastThemeChangedAt = changedAt;
            Theme.IsDark = isDark;
            Add("THEME", isDark ? "Dark theme applied" : "Light theme applied", source);
        });
    }

    private void RefreshHeartbeat()
    {
        if (_client?.LastHeartbeatAt is not DateTimeOffset heartbeat) { HeartbeatLabel = "No heartbeat"; return; }
        var age = DateTimeOffset.UtcNow - heartbeat;
        HeartbeatLabel = age.TotalSeconds < 3 ? $"Live · {age.TotalSeconds:0.0}s ago" : $"Stale · {age.TotalSeconds:0}s ago";
    }

    private void Add(string kind, string title, string detail)
    {
        Activity.Insert(0, new(DateTimeOffset.Now, kind, title, detail));
        while (Activity.Count > 80) Activity.RemoveAt(Activity.Count - 1);
    }

    private async Task DisconnectCoreAsync()
    {
        _themeLifetime?.Cancel();
        _eventLifetime?.Cancel();
        if (_themeReader is not null) await _themeReader;
        if (_eventReader is not null) await _eventReader;
        if (_themeSubscription is not null) await _themeSubscription.DisposeAsync();
        if (_subscription is not null) await _subscription.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
        _themeSubscription = null; _themeReader = null;
        _themeLifetime?.Dispose(); _themeLifetime = null;
        _lastThemeChangedAt = DateTimeOffset.MinValue;
        _subscription = null; _client = null; _eventReader = null;
        _eventLifetime?.Dispose(); _eventLifetime = null;
        IsSubscribed = false; IsConnected = false; ConnectionLabel = "OFFLINE"; HeartbeatLabel = "No heartbeat";
        Add("CLOSE", "Connection closed", "Client resources were disposed.");
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatTimer.Stop();
        _requestLifetime?.Cancel();
        await DisconnectCoreAsync();
    }
}
