using System.Collections.Concurrent;
using System.Diagnostics;
using Liftoff.Ipc;

namespace IpcDemo.Tests.Integration;

public sealed class IpcBehaviorTests
{
    [Fact]
    public async Task Request_completes_and_reports_progress()
    {
        await using var app = await TestApplication.StartAsync();
        await using var client = await app.ConnectClientAsync();
        var progress = new RecordingProgress<IpcProgress>();
        var request = new Analyze("Integration Model", Steps: 4, DelayMilliseconds: 10);

        var result = await client.RequestAsync(request, progress);

        Assert.Equal("Integration Model", result.ModelName);
        Assert.Equal(500, result.ElementsAnalyzed);
        Assert.Equal(4, progress.Values.Count);
        Assert.Equal(100, progress.Values[^1].Percent);
    }

    [Fact]
    public async Task Handler_failure_is_reported_to_the_caller()
    {
        await using var app = await TestApplication.StartAsync();
        await using var client = await app.ConnectClientAsync();
        var request = new Analyze("Broken Model", ShouldFail: true);

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.RequestAsync(request));

        Assert.Equal("The test operation was asked to fail.", exception.Message);
    }

    [Fact]
    public async Task Caller_can_cancel_a_running_operation()
    {
        await using var app = await TestApplication.StartAsync();
        await using var client = await app.ConnectClientAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var request = new Analyze("Large Model", Steps: 100, DelayMilliseconds: 20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RequestAsync(request, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Server_accepts_a_new_client_after_the_first_disconnects()
    {
        await using var app = await TestApplication.StartAsync();

        await using (var firstClient = await app.ConnectClientAsync())
        {
            var first = await firstClient.RequestAsync(new Analyze("First Client"));
            Assert.Equal("First Client", first.ModelName);
        }

        await using var secondClient = await app.ConnectClientAsync();

        var second = await secondClient.RequestAsync(new Analyze("Second Client"));

        Assert.Equal("Second Client", second.ModelName);
    }

    [Fact]
    public async Task Events_are_delivered_only_while_subscribed()
    {
        await using var app = await TestApplication.StartAsync();
        await using var client = await app.ConnectClientAsync();
        _ = await client.RequestAsync(new Analyze("Connected Model"));
        var eventData = new ItemChanged(7, "Wall-42");

        var beforeSubscription = await app.Server.PublishAsync(eventData);
        await using var subscription = await client.SubscribeAsync<ItemChanged>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = subscription.GetAsyncEnumerator(timeout.Token);
        var nextEvent = events.MoveNextAsync().AsTask();
        var whileSubscribed = await app.Server.PublishAsync(eventData);

        Assert.True(await nextEvent);
        await subscription.DisposeAsync();
        var afterUnsubscription = await app.Server.PublishAsync(eventData);

        Assert.Equal(0, beforeSubscription);
        Assert.Equal(1, whileSubscribed);
        Assert.Equal(0, afterUnsubscription);
        Assert.Equal(eventData, events.Current);
    }

    [Fact]
    public async Task Request_without_a_registered_handler_is_rejected()
    {
        await using var app = await TestApplication.StartAsync();
        await using var client = await app.ConnectClientAsync();

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.RequestAsync(new UnknownRequest()));

        Assert.Contains("No handler is registered", exception.Message);
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _values = new();
        public IReadOnlyList<T> Values => _values.ToArray();
        public void Report(T value) => _values.Enqueue(value);
    }

    private sealed class TestApplication : IAsyncDisposable
    {
        private TestApplication(string pipeName, IpcServer server)
        {
            PipeName = pipeName;
            Server = server;
        }

        public string PipeName { get; }
        public IpcServer Server { get; }

        public static async Task<TestApplication> StartAsync()
        {
            var pipeName = $"Liftoff.Ipc.Tests.{Guid.NewGuid():N}";
            var server = IpcServer.Create(pipeName);
            server.RegisterHandlersFromAssemblyContaining<AnalyzeHandler>();
            await server.StartAsync();
            return new TestApplication(pipeName, server);
        }

        public Task<IpcClient> ConnectClientAsync() =>
            IpcClient.ConnectAsync(PipeName);

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }
}

public sealed record Analyze(
    string ModelName,
    int Steps = 1,
    int DelayMilliseconds = 1,
    bool ShouldFail = false) : IIpcRequest<AnalysisResult>;

public sealed record AnalysisResult(
    string ModelName,
    int ElementsAnalyzed,
    TimeSpan Elapsed);

public sealed record ItemChanged(int ElementId, string ElementName) : IIpcEvent;

public sealed record UnknownRequest : IIpcRequest<IpcUnit>;

public sealed class AnalyzeHandler : IIpcRequestHandler<Analyze, AnalysisResult>
{
    public async ValueTask<AnalysisResult> HandleAsync(
        Analyze request,
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        for (var step = 1; step <= request.Steps; step++)
        {
            await Task.Delay(request.DelayMilliseconds, cancellationToken);
            await context.ReportProgressAsync(
                step * 100d / request.Steps,
                $"Step {step} of {request.Steps}.",
                cancellationToken);
        }

        if (request.ShouldFail)
        {
            throw new InvalidOperationException("The test operation was asked to fail.");
        }

        return new AnalysisResult(
            request.ModelName,
            request.Steps * 125,
            stopwatch.Elapsed);
    }
}
