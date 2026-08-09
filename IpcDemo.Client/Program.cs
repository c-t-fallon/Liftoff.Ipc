using IpcDemo.Contracts;
using Liftoff.Ipc;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await using IIpcClient client = await IpcClient.ConnectAsync(
    DemoIpc.PipeName,
    shutdown.Token);

var shouldFail = args.Contains("--fail", StringComparer.OrdinalIgnoreCase);
var showEvents = args.Contains("--events", StringComparer.OrdinalIgnoreCase);
var cancelAfterArgument = args.FirstOrDefault(argument =>
    argument.StartsWith("--cancel-after=", StringComparison.OrdinalIgnoreCase));
if (cancelAfterArgument is not null
    && int.TryParse(cancelAfterArgument.Split('=', 2)[1], out var cancelAfterMilliseconds))
{
    shutdown.CancelAfter(cancelAfterMilliseconds);
}

var request = new AnalyzeModelRequest(
    "Learning Model",
    Steps: 8,
    DelayMilliseconds: 400,
    ShouldFail: shouldFail);

var progress = new Progress<IpcProgress>(update =>
    Console.WriteLine($"[{update.Percent,3:0}%] {update.Message}"));
using var eventLifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
await using var eventSubscription = showEvents
    ? await client.SubscribeAsync<ModelChanged>(shutdown.Token)
    : null;
var eventReader = eventSubscription is null
    ? Task.CompletedTask
    : PrintEventsAsync(eventSubscription, eventLifetime.Token);

try
{
    Console.WriteLine("Connecting and submitting work (Ctrl+C requests cancellation)...");

    // The application sees an ordinary async method call. Frames, correlation IDs,
    // acceptance, and response dispatch remain implementation details.
    var result = await client.RequestAsync(request, progress, shutdown.Token);

    Console.WriteLine();
    Console.WriteLine($"Completed: {result.ElementsAnalyzed} elements in {result.Elapsed.TotalSeconds:0.0}s.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("The operation was cancelled.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Request failed: {exception.Message}");
    Environment.ExitCode = 1;
}
finally
{
    await eventLifetime.CancelAsync();
    await eventReader;
}

static async Task PrintEventsAsync(
    IpcSubscription<ModelChanged> subscription,
    CancellationToken cancellationToken)
{
    try
    {
        await foreach (var modelChanged in subscription.WithCancellation(cancellationToken))
        {
            Console.WriteLine($"[event] {modelChanged.ElementName} changed.");
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // The request finished or the user pressed Ctrl+C.
    }
}
