using System.Runtime.Serialization;
using Liftoff.Ipc;
using Liftoff.Ipc.Internal;

namespace IpcDemo.Tests.Unit;

public sealed class IpcClientTests
{
    [Fact]
    public async Task Successful_remote_operation_returns_its_result()
    {
        await using var transport = new ControllableTransport();
        await using var client = await CreateClientAsync(transport);

        var requestTask = client.RequestAsync(new Analyze("Office Model"));
        var request = await transport.NextSentMessageAsync();
        transport.Deliver(Accepted(request));
        transport.Deliver(Completed(request, "Office Model", 750));

        var result = await requestTask;

        Assert.Equal("Office Model", result.ModelName);
        Assert.Equal(750, result.ElementsAnalyzed);
    }

    [Fact]
    public async Task Failed_remote_operation_is_reported_as_an_exception()
    {
        await using var transport = new ControllableTransport();
        await using var client = await CreateClientAsync(transport);

        var requestTask = client.RequestAsync(new Analyze("Broken Model"));
        var request = await transport.NextSentMessageAsync();
        transport.Deliver(Accepted(request));
        transport.Deliver(IpcEnvelope.Create(
            MessageTypes.OperationFailed,
            request.RequestId,
            new OperationFailed("Model analysis failed.")));

        var exception = await Assert.ThrowsAsync<IpcRemoteException>(() => requestTask);

        Assert.Equal("Model analysis failed.", exception.Message);
    }

    [Fact]
    public async Task Request_not_accepted_before_deadline_times_out()
    {
        await using var transport = new ControllableTransport();
        await using var client = await CreateClientAsync(
            transport,
            TimeSpan.FromMilliseconds(50));

        var requestTask = client.RequestAsync(new Analyze("Unacknowledged Model"));
        _ = await transport.NextSentMessageAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => requestTask);
    }

    [Fact]
    public async Task Caller_cancellation_cancels_the_pending_request()
    {
        await using var transport = new ControllableTransport();
        await using var client = await CreateClientAsync(transport);
        using var cancellation = new CancellationTokenSource();

        var requestTask = client.RequestAsync(
            new Analyze("Large Model"),
            cancellationToken: cancellation.Token);
        var request = await transport.NextSentMessageAsync();
        transport.Deliver(Accepted(request));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
    }

    [Fact]
    public async Task Concurrent_requests_receive_their_own_results()
    {
        await using var transport = new ControllableTransport();
        await using var client = await CreateClientAsync(transport);

        var firstTask = client.RequestAsync(new Analyze("First Model"));
        var secondTask = client.RequestAsync(new Analyze("Second Model"));
        var firstRequest = await transport.NextSentMessageAsync();
        var secondRequest = await transport.NextSentMessageAsync();

        transport.Deliver(Accepted(secondRequest));
        transport.Deliver(Completed(secondRequest, "Second Model", 200));
        transport.Deliver(Accepted(firstRequest));
        transport.Deliver(Completed(firstRequest, "First Model", 100));

        var first = await firstTask;
        var second = await secondTask;

        Assert.Equal("First Model", first.ModelName);
        Assert.Equal(100, first.ElementsAnalyzed);
        Assert.Equal("Second Model", second.ModelName);
        Assert.Equal(200, second.ElementsAnalyzed);
    }

    [Fact]
    public async Task Subscribed_event_is_delivered_as_a_typed_value()
    {
        await using var transport = new ControllableTransport();
        await using var client = await CreateClientAsync(transport);

        var subscribeTask = client.SubscribeAsync<ModelChanged>();
        var request = await transport.NextSentMessageAsync();
        transport.Deliver(IpcEnvelope.Create(
            MessageTypes.SubscriptionAccepted,
            request.RequestId,
            new SubscriptionAccepted(ContractName.For<ModelChanged>(), DateTimeOffset.UtcNow)));
        await using var subscription = await subscribeTask;
        await using var events = subscription.GetAsyncEnumerator();

        var nextEvent = events.MoveNextAsync();
        var expected = new ModelChanged(42, "Wall-17");
        transport.Deliver(IpcEnvelope.Create(
            MessageTypes.EventPublished,
            request.RequestId,
            new EventPublished(IpcSerializer.Serialize(expected))));

        Assert.True(await nextEvent);
        Assert.Equal(expected, events.Current);
    }

    private static Task<IpcClient> CreateClientAsync(
        IIpcTransport transport,
        TimeSpan? acknowledgementTimeout = null) =>
        IpcClient.ConnectAsync(
            transport,
            new IpcClientOptions
            {
                AcknowledgementTimeout = acknowledgementTimeout ?? TimeSpan.FromSeconds(1)
            },
            CancellationToken.None);

    private static IpcEnvelope Accepted(IpcEnvelope request) =>
        IpcEnvelope.Create(
            MessageTypes.RequestAccepted,
            request.RequestId,
            new RequestAccepted(DateTimeOffset.UtcNow));

    private static IpcEnvelope Completed(
        IpcEnvelope request,
        string modelName,
        int elementsAnalyzed)
    {
        var result = new AnalysisResult(modelName, elementsAnalyzed);
        return IpcEnvelope.Create(
            MessageTypes.OperationCompleted,
            request.RequestId,
            new OperationCompleted(IpcSerializer.Serialize(result)));
    }

    [DataContract]
    public sealed record Analyze(
        [property: DataMember(Order = 1)] string ModelName) : IIpcRequest<AnalysisResult>;
    [DataContract]
    public sealed record AnalysisResult(
        [property: DataMember(Order = 1)] string ModelName,
        [property: DataMember(Order = 2)] int ElementsAnalyzed);
    [DataContract]
    public sealed record ModelChanged(
        [property: DataMember(Order = 1)] int ElementId,
        [property: DataMember(Order = 2)] string ElementName) : IIpcEvent;

    private sealed class ControllableTransport : IIpcTransport
    {
        private readonly AsyncQueue<IpcEnvelope> _sent = new();
        private readonly AsyncQueue<IpcEnvelope> _received = new();

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAsync(IpcEnvelope message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(_sent.TryEnqueue(message));
            if (message.Type == MessageTypes.UnsubscribeRequest)
            {
                Assert.True(_received.TryEnqueue(
                    IpcEnvelope.Create(
                        MessageTypes.UnsubscriptionAccepted,
                        message.RequestId,
                        new UnsubscriptionAccepted(DateTimeOffset.UtcNow))));
            }

            return Task.CompletedTask;
        }

        public async Task<IpcEnvelope?> ReadAsync(CancellationToken cancellationToken = default) =>
            (await _received.ReadAsync(cancellationToken)).Item;

        public async Task<IpcEnvelope> NextSentMessageAsync() =>
            (await _sent.ReadAsync()).Item;
        public void Deliver(IpcEnvelope message) => Assert.True(_received.TryEnqueue(message));

        public Task DisposeAsync()
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _sent.Complete();
            _received.Complete();
        }
    }
}
