using IpcDemo.Contracts;
using IpcDemo.Server;
using Liftoff.Ipc;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine("IPC demo server. Press Ctrl+C to stop.");
await using var server = IpcServer.Create(DemoIpc.PipeName);
server.RegisterHandlersFromAssemblyContaining<AnalyzeModelHandler>();

try
{
    await server.StartAsync(shutdown.Token);
    await PublishModelChangesAsync(server, shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Console.WriteLine("Server stopped.");
}

static async Task PublishModelChangesAsync(
    IpcServer publisher,
    CancellationToken cancellationToken)
{
    var sequence = 0;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

    while (await timer.WaitForNextTickAsync(cancellationToken))
    {
        sequence++;
        await publisher.PublishAsync(
            new ModelChanged(sequence, $"Element-{sequence}", DateTimeOffset.UtcNow),
            cancellationToken);
    }
}
