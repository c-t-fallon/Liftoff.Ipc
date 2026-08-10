using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Serialization;
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
        Assert.Equal(100, progress.Values[progress.Values.Count - 1].Percent);
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
        var nextEvent = events.MoveNextAsync();
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

    [Fact]
    public async Task Unauthenticated_client_is_rejected_without_disrupting_the_server()
    {
        var session = IpcSession.Create();
        await using var server = IpcServer.Create(
            session,
            options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(500));
        server.RegisterHandlersFromAssemblyContaining<AnalyzeHandler>();
        await server.StartAsync();
        await using var unauthenticatedClient = await IpcClient.ConnectAsync(session.PipeName);

        await Assert.ThrowsAnyAsync<IpcException>(() =>
            unauthenticatedClient.RequestAsync(new Analyze("Rogue Client")));

        await using var authenticatedClient = await IpcClient.ConnectAsync(session);
        var result = await authenticatedClient.RequestAsync(new Analyze("Trusted Client"));

        Assert.Equal("Trusted Client", result.ModelName);
    }

    [Fact]
    public async Task Authenticated_client_rejects_a_server_without_the_session_key()
    {
        var session = IpcSession.Create();
        await using var unauthenticatedServer = IpcServer.Create(session.PipeName);
        await unauthenticatedServer.StartAsync();
        var options = new IpcClientOptions
        {
            AuthenticationTimeout = TimeSpan.FromMilliseconds(200)
        };

        var exception = await Assert.ThrowsAsync<IpcAuthenticationException>(() =>
            IpcClient.ConnectAsync(session, options));

        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Client_with_a_different_session_key_is_rejected()
    {
        var serverSession = IpcSession.Create();
        var clientSession = IpcSession.Create(serverSession.PipeName);
        await using var server = IpcServer.Create(serverSession);
        await server.StartAsync();

        await Assert.ThrowsAsync<IpcAuthenticationException>(() =>
            IpcClient.ConnectAsync(clientSession));
    }

    [Fact]
    public void Session_can_configure_a_child_without_printing_its_secret()
    {
        var session = IpcSession.Create();
        var startInfo = new ProcessStartInfo("child.exe");

        session.ConfigureChildProcess(startInfo);

        var encodedKey = startInfo.EnvironmentVariables[
            IpcSession.AuthenticationKeyEnvironmentVariable];
        Assert.Equal(
            session.PipeName,
            startInfo.EnvironmentVariables[IpcSession.PipeNameEnvironmentVariable]);
        Assert.NotNull(encodedKey);
        Assert.Equal(32, Convert.FromBase64String(encodedKey).Length);
        Assert.DoesNotContain(encodedKey, session.ToString());
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        private readonly ConcurrentQueue<T> _values = new();
        public IReadOnlyList<T> Values => _values.ToArray();
        public void Report(T value) => _values.Enqueue(value);
    }

    private sealed class TestApplication
    {
        private TestApplication(IpcSession session, IpcServer server)
        {
            Session = session;
            Server = server;
        }

        public IpcSession Session { get; }
        public IpcServer Server { get; }

        public static async Task<TestApplication> StartAsync()
        {
            var session = IpcSession.Create();
            var server = IpcServer.Create(session);
            server.RegisterHandlersFromAssemblyContaining<AnalyzeHandler>();
            await server.StartAsync();
            return new TestApplication(session, server);
        }

        public Task<IpcClient> ConnectClientAsync() =>
            IpcClient.ConnectAsync(Session);

        public Task DisposeAsync() => Server.DisposeAsync();
    }
}

[DataContract]
public sealed record Analyze(
    [property: DataMember(Order = 1)] string ModelName,
    [property: DataMember(Order = 2)] int Steps = 1,
    [property: DataMember(Order = 3)] int DelayMilliseconds = 1,
    [property: DataMember(Order = 4)] bool ShouldFail = false) : IIpcRequest<AnalysisResult>;

[DataContract]
public sealed record AnalysisResult(
    [property: DataMember(Order = 1)] string ModelName,
    [property: DataMember(Order = 2)] int ElementsAnalyzed,
    [property: DataMember(Order = 3)] TimeSpan Elapsed);

[DataContract]
public sealed record ItemChanged(
    [property: DataMember(Order = 1)] int ElementId,
    [property: DataMember(Order = 2)] string ElementName) : IIpcEvent;

[DataContract]
public sealed record UnknownRequest : IIpcRequest<IpcUnit>;

public sealed class AnalyzeHandler : IIpcRequestHandler<Analyze, AnalysisResult>
{
    public async Task<AnalysisResult> HandleAsync(
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
