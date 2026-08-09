# Liftoff.Ipc

`Liftoff.Ipc` is a small library for typed request/response and server-published events between local .NET processes over Windows named pipes. The demo models a parent application hosting the server and a distributed child executable connecting as its client.

The NuGet package targets .NET Framework 4.8, .NET 8, and .NET 10. Endpoints may use different target frameworks: for example, a Revit 2024 add-in on .NET Framework 4.8 can communicate with a child process on .NET 8 while both reference the corresponding build of the same contracts package.

The library keeps correlation IDs, acknowledgement timeouts, framing, serialization, heartbeats, cancellation, subscriptions, and pipe lifecycle behind one public namespace:

```csharp
using Liftoff.Ipc;
```

## Authenticated sessions

For parent/child applications, create one authenticated session before launching the child. A session uses an unpredictable pipe name, a 256-bit key, current-user pipe isolation, and a mutual HMAC-SHA256 handshake. The key is never sent over the pipe, and application messages are not accepted until both endpoints authenticate.

```csharp
var session = IpcSession.Create();
var child = new ProcessStartInfo("Child.exe");
session.ConfigureChildProcess(child);
Process.Start(child);

await using var server = IpcServer.Create(session);
```

The child reads the session from its inherited environment and may act as either client or server:

```csharp
var session = IpcSession.FromEnvironment();
await using var client = await IpcClient.ConnectAsync(session);
```

Fixed-name overloads remain available for independently configured applications. They restrict access to the current Windows user by default but do not authenticate same-user peers; authenticated `IpcSession` overloads are recommended whenever one process launches the other.

## Shared contracts

Parent and child reference the same small contract assembly. Requests declare their response type, while events use a marker interface:

```csharp
public sealed record AnalyzeModel(string ModelName)
    : IIpcRequest<AnalysisResult>;

public sealed record AnalysisResult(int ElementsAnalyzed);

public sealed record ModelChanged(int ElementId)
    : IIpcEvent;
```

No manual message names or contract versions are required. The library derives an internal identity from the shared CLR type's assembly name and full name. This fits applications where the parent, child, contracts, and IPC library are distributed as one aligned unit.

## Server

Handlers follow a mediator-style interface and may report progress:

```csharp
public sealed class AnalyzeModelHandler
    : IIpcRequestHandler<AnalyzeModel, AnalysisResult>
{
    public async ValueTask<AnalysisResult> HandleAsync(
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

## Tests

```powershell
dotnet test IpcDemo.slnx
```

- `IpcDemo.Tests.Integration` runs on .NET Framework 4.8, .NET 8, and .NET 10 and tests only the public library API through real operating-system named pipes. Test-owned CLR contracts and handlers exercise reflection discovery, requests, progress, errors, cancellation, reconnection, event subscription, publication, and acknowledged unsubscription.
- `IpcDemo.Tests.Unit` covers internal framing and request coordination in memory. These focused implementation tests are deliberately separate from the stable public behavior suite.

## Deliberate boundary

The library does not automatically replay an in-flight request after a broken connection. If a connection dies after a destructive command was sent but before its result arrives, the client cannot know whether it executed. Safe retry requires an application-specific idempotency policy.

Likely future production concerns include named-pipe access control, bounded queues/backpressure, structured diagnostics, and optional parent/child process supervision. They are not required by the current public API.
