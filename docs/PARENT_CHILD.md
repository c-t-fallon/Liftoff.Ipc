# Parent/child applications

The primary `Liftoff.Ipc` deployment model is a host application that owns an IPC server and launches a companion executable as its client. For example, a Revit add-in can host the server inside Revit and distribute a separate executable for its user interface or isolated work.

In this model, users should not select a pipe, start a listener, exchange a secret, or click a connect button. The parent creates the connection context, passes it only to the child, and owns both processes' lifecycle.

## Recommended lifecycle

The parent should perform these steps in order:

1. Create an `IpcSession`. Its default pipe name is unique, so separate host instances do not collide.
2. Create the `IpcServer` from that session and register the application's handlers.
3. Start the server. Starting first ensures the listener is ready before the child attempts to connect.
4. Create the child's `ProcessStartInfo` and call `session.ConfigureChildProcess(startInfo)`.
5. Launch and retain the returned `Process` so the application can observe and stop the child.
6. On normal parent shutdown, request a graceful child exit and apply the application's timeout or forced-termination policy.

```csharp
using System.Diagnostics;
using Liftoff.Ipc;

var session = IpcSession.Create();

await using var server = IpcServer.Create(session);
server.RegisterHandlersFromAssemblyContaining<AnalyzeModelHandler>();
await server.StartAsync(parentCancellationToken);

var startInfo = new ProcessStartInfo(childExecutablePath)
{
    WorkingDirectory = Path.GetDirectoryName(childExecutablePath)!
};

session.ConfigureChildProcess(startInfo);

using var child = Process.Start(startInfo)
    ?? throw new InvalidOperationException("The child process did not start.");
```

`ConfigureChildProcess` sets `UseShellExecute` to `false` and places the session's pipe name and authentication key in the new process's environment. There is no command-line secret to log or configuration file to distribute. The same `IpcSession` instance must be used to create the parent endpoint and configure the child.

The child reconstructs the session and connects:

```csharp
using Liftoff.Ipc;

var session = IpcSession.FromEnvironment();
await using var client = await IpcClient.ConnectAsync(
    session,
    applicationStoppingToken);
```

`TryFromEnvironment` is useful when one executable supports both parent-launched and manually configured modes:

```csharp
IpcClient client;

if (IpcSession.TryFromEnvironment(out var parentSession))
{
    client = await IpcClient.ConnectAsync(parentSession!, cancellationToken);
}
else
{
    client = await IpcClient.ConnectAsync(configuredPipeName, cancellationToken);
}
```

Partially present or malformed session environment variables are treated as configuration errors. This prevents a child from silently falling back to a less secure connection after an incorrectly configured launch.

## What authentication provides

An authenticated session combines:

- an unpredictable per-session pipe name;
- Windows current-user pipe isolation;
- a randomly generated 256-bit authentication key; and
- a mutual HMAC-SHA256 handshake before application messages are accepted.

The authentication key is inherited through the child environment and is never transmitted over the named pipe. A fixed-name connection still receives current-user isolation by default, but it cannot distinguish the intended child from another process running as that Windows user. Use `IpcSession` whenever one endpoint launches the other.

An `IpcSession` establishes endpoint identity; it does not encrypt application payloads. Named pipes are local to the machine, and the current-user boundary is the package's default access boundary.

## Revit integration

For a Revit deployment, the usual ownership is:

- Revit loads the add-in and hosts the `IpcServer`.
- The add-in creates one session for each companion executable it launches.
- The companion executable calls `IpcSession.FromEnvironment` and connects as the client.
- Both projects reference a small shared contracts assembly and the build of `Liftoff.Ipc` appropriate for their target framework.

The endpoints may target different supported frameworks. For example, a Revit 2024 add-in can use .NET Framework 4.8 while its child executable uses .NET 8, provided both sides deploy compatible copies of the same contracts assembly.

IPC handlers do not execute on Revit's UI/API thread. A handler that needs the Revit API should marshal the work through Revit's `ExternalEvent` mechanism and complete its task when that work finishes. This keeps Revit-specific scheduling in the add-in and out of the transport library.

Revit can open and close multiple documents during one process lifetime. Choose session ownership deliberately: an application-scoped child can share the add-in's lifetime, while document-specific children should be stopped when their owning document closes.

## Process supervision

`Liftoff.Ipc` does not start, restart, or terminate the child. Those policies depend on the host application and belong beside its process-launch code.

A typical normal-shutdown sequence is:

1. Ask the child to close, or call `Process.CloseMainWindow` for a desktop child.
2. Allow a short grace period with `WaitForExitAsync`.
3. If the child does not exit, terminate it according to the host application's policy.
4. Dispose the server to disconnect any remaining IPC session and release the pipe.

Holding a `Process` object does not make Windows terminate the child if the parent crashes or is forcibly killed. Applications that require crash-coupled lifetime should additionally use a Windows Job Object or another platform-specific supervisor. That stronger guarantee is separate from the IPC connection itself.

The WPF server demo shows a lightweight policy: it starts the authenticated server, launches the client from its bundled output folder, tracks exit events, requests a graceful close when the server stops, and terminates the process tree after a short timeout.

## Deployment

Deploy the child executable and all of its runtime files at a location known to the parent. The parent should resolve that location itself rather than accept an arbitrary executable path from the IPC client.

The WPF demo's server build copies the client output into a `Client` directory under the server output. A production Revit installer can use the same conceptual layout inside the add-in installation directory. The library imposes no required directory structure.

Keep the parent, child, shared contracts, and IPC library versions aligned as one product release. Contract identities are derived from the shared CLR type's assembly and full names, so renaming or moving a contract type is a wire-level change for already deployed endpoints.
