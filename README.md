# Liftoff.Ipc

`Liftoff.Ipc` is a small library for typed request/response and server-published events between local .NET processes over Windows named pipes. The demo models a parent application hosting the server and a distributed child executable connecting as its client.

The dependency-free NuGet package targets .NET Framework 4.8, .NET 8, and .NET 10. Endpoints may use different target frameworks: for example, a Revit 2024 add-in on .NET Framework 4.8 can communicate with a child process on .NET 8 while both reference the corresponding build of the same contracts package.

Release maintainers should follow the [release runbook](docs/RELEASING.md). Packaging and publishing are intentionally owned by GitHub Actions rather than the library project.

The library keeps correlation IDs, acknowledgement timeouts, framing, serialization, heartbeats, cancellation, subscriptions, and pipe lifecycle behind one public namespace:

```csharp
using Liftoff.Ipc;
```

## Parent/child applications (primary use case)

`Liftoff.Ipc` is designed first for applications where a host process owns the server and launches a companion executable as its client. A Revit add-in is a typical example: Revit hosts the server in-process, while an executable distributed with the add-in provides the client UI or performs isolated work.

The recommended lifecycle is:

1. The parent creates a unique authenticated `IpcSession`.
2. The parent creates and starts the server before launching the child.
3. The parent adds the session to the child's environment with `ConfigureChildProcess`.
4. The child reads the inherited session and connects without pipe names, keys, ports, or configuration files supplied by the user.
5. The application owns child-process supervision and shuts the child down with the parent.

A session uses an unpredictable pipe name, a 256-bit key, current-user pipe isolation, and a mutual HMAC-SHA256 handshake. The key is never sent over the pipe, and application messages are not accepted until both endpoints authenticate.

```csharp
var session = IpcSession.Create();

await using var server = IpcServer.Create(session);
server.RegisterHandlersFromAssemblyContaining<AnalyzeModelHandler>();
await server.StartAsync(parentCancellationToken);

var child = new ProcessStartInfo("Child.exe");
session.ConfigureChildProcess(child);
using var childProcess = Process.Start(child)
    ?? throw new InvalidOperationException("The child process did not start.");
```

The child reads the session from its inherited environment and connects automatically:

```csharp
var session = IpcSession.FromEnvironment();
await using var client = await IpcClient.ConnectAsync(session);
```

`Liftoff.Ipc` transports messages but deliberately does not launch, restart, or terminate processes. The parent application decides how the child executable is located and what graceful or forced shutdown policy it needs. The WPF demo includes one practical implementation.

Fixed-name overloads remain available for independently configured applications. They restrict access to the current Windows user by default but do not authenticate same-user peers; authenticated `IpcSession` overloads are recommended whenever one process launches the other. See the [parent/child application guide](docs/PARENT_CHILD.md) for a complete lifecycle, deployment guidance, Revit integration notes, and shutdown considerations.

## Shared contracts

Parent and child reference the same small contract assembly. Requests declare their response type, while events use a marker interface:

```csharp
using System.Runtime.Serialization;

[DataContract]
public sealed record AnalyzeModel(
    [property: DataMember(Order = 1)] string ModelName)
    : IIpcRequest<AnalysisResult>;

[DataContract]
public sealed record AnalysisResult(
    [property: DataMember(Order = 1)] int ElementsAnalyzed);

[DataContract]
public sealed record ModelChanged(
    [property: DataMember(Order = 1)] int ElementId)
    : IIpcEvent;
```

Explicit data contracts keep the binary wire representation stable between .NET Framework and modern .NET without bringing serializer packages into a Revit process.

No manual message names or contract versions are required. The library derives an internal identity from the shared CLR type's assembly name and full name. This fits applications where the parent, child, contracts, and IPC library are distributed as one aligned unit.

## Server

Handlers follow a mediator-style interface and may report progress:

```csharp
public sealed class AnalyzeModelHandler
    : IIpcRequestHandler<AnalyzeModel, AnalysisResult>
{
    public async Task<AnalysisResult> HandleAsync(
        AnalyzeModel request,
        IpcRequestContext context,
        CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync(50, "Halfway", cancellationToken);
        return new AnalysisResult(1_000);
    }
}
```

Create the server, discover handlers, and start listening:

```csharp
await using var server = IpcServer.Create("my-product.pipe");

server.RegisterHandlersFromAssemblyContaining<AnalyzeModelHandler>();
await server.StartAsync(cancellationToken);
```

Handler discovery scans only concrete implementations of `IIpcRequestHandler<TRequest,TResponse>`. Explicit instance and delegate registration are also available. A handler factory can integrate any dependency-injection container without making the library depend on one.

## Client

```csharp
await using var client = await IpcClient.ConnectAsync(
    "my-product.pipe",
    cancellationToken);

var progress = new Progress<IpcProgress>(update =>
    Console.WriteLine($"{update.Percent}%: {update.Message}"));

var result = await client.RequestAsync(
    new AnalyzeModel("Office Model"),
    progress,
    cancellationToken);
```

The acknowledgement timeout answers only whether the server received and queued a request. Completion has no arbitrary transport timeout; callers provide an operation-specific cancellation token when appropriate.

## Events

Clients explicitly subscribe to an event type:

```csharp
await using var subscription =
    await client.SubscribeAsync<ModelChanged>(cancellationToken);

await foreach (var changed in subscription.WithCancellation(cancellationToken))
{
    Console.WriteLine(changed.ElementId);
}
```

Server/domain code publishes the shared event type:

```csharp
var recipients = await server.PublishAsync(
    new ModelChanged(elementId),
    cancellationToken);
```

`recipients` is zero when no connected client subscribed. Disposing a subscription sends an unsubscribe message, waits for acknowledgement, and completes its local async stream. Subscriptions belong to one connection and must be recreated after reconnecting.

## Solution boundaries

- `Liftoff.Ipc` contains the reusable public API and all internal named-pipe/protocol machinery.
- `IpcDemo.Contracts` is an example application contract assembly shared by the demo parent and child.
- `IpcDemo.Server` contains application handlers and server startup only.
- `IpcDemo.Client` contains client application code only.
- `IpcDemo.Wpf.Server` and `IpcDemo.Wpf.Client` are visual learning tools built with native WPF controls and CommunityToolkit.Mvvm. Their shared presentation models live in `IpcDemo.Wpf.Shared`.

The server's pipe reader never runs application handlers. It acknowledges and queues requests so transport processing remains responsive. A single queue consumer invokes handlers; a Revit application can make its handler delegate into `ExternalEvent` without introducing a Revit dependency into this library.

## Run the demo

In one terminal:

```powershell
dotnet run --project IpcDemo.Server --framework net10.0
```

In another:

```powershell
dotnet run --project IpcDemo.Client --framework net10.0
```

Additional scenarios:

```powershell
dotnet run --project IpcDemo.Client --framework net10.0 -- --fail
dotnet run --project IpcDemo.Client --framework net10.0 -- --cancel-after=1200
dotnet run --project IpcDemo.Client --framework net10.0 -- --events
```

For the visual demo, run the server station and click **Start + launch client**. The server creates an authenticated IPC session, launches the client as a tracked child process, and the client connects automatically. Stopping or closing the server also closes the launched client.

```powershell
dotnet run --project IpcDemo.Wpf.Server
```

The original manual workflow remains available: click **Start listening**, run `dotnet run --project IpcDemo.Wpf.Client` in another terminal, and click **Connect**.

The client exposes progress, cancellation, remote failure, heartbeats, and typed event subscriptions. The server shows handler work, event publication, and subscriber delivery counts. Changing the server theme publishes a `ThemeChanged` event so connected clients follow it; clients also request a theme snapshot when connecting so reconnects cannot miss earlier changes. Both apps use native light and dark themes; no commercial or third-party WPF control suite is required.

## Tests

```powershell
dotnet test IpcDemo.slnx
```

- `IpcDemo.Tests.Integration` runs on .NET Framework 4.8, .NET 8, and .NET 10 and tests only the public library API through real operating-system named pipes. Test-owned CLR contracts and handlers exercise reflection discovery, requests, progress, errors, cancellation, reconnection, event subscription, publication, and acknowledged unsubscription.
- `IpcDemo.Tests.Unit` covers internal framing and request coordination in memory. These focused implementation tests are deliberately separate from the stable public behavior suite.

## Deliberate boundary

The library does not automatically replay an in-flight request after a broken connection. If a connection dies after a destructive command was sent but before its result arrives, the client cannot know whether it executed. Safe retry requires an application-specific idempotency policy.

Likely future production concerns include additional named-pipe access policies, bounded queues/backpressure, and structured diagnostics. Parent/child process supervision intentionally remains an application concern rather than part of the IPC API.
