# Parent/child applications

The primary `Liftoff.Ipc` deployment model is a host application that owns an IPC server and launches a child executable as its client. For example, a Revit add-in can host the server inside Revit and distribute a separate executable for its user interface or isolated work.

In this model, users should not select a pipe, start a listener, exchange a secret, or click a connect button. The parent creates the connection context, passes it only to the child, and owns both processes' lifecycle.

## Recommended lifecycle

`IpcChildProcessHost` performs the standard lifecycle in order:

1. Create a unique authenticated `IpcSession`.
2. Create the `IpcServer` and register the application's handlers.
3. Start the server so its listener is ready before the child connects.
4. Add the session to a new `ProcessStartInfo` and launch the child.
5. Track independent child exit.
6. On shutdown, close the child's main window, wait for the configured grace period, and force termination if necessary.

```csharp
using Liftoff.Ipc;

await using var host = new IpcChildProcessHost(
    childExecutablePath,
    options => options.ShutdownTimeout = TimeSpan.FromSeconds(2));

await host.StartAsync(server =>
    server.RegisterHandlersFromAssemblyContaining<AnalyzeModelHandler>(),
    parentCancellationToken);

// Use RestartAsync when replacing an existing child is intentional.
await host.RestartAsync(server =>
    server.RegisterHandlersFromAssemblyContaining<AnalyzeModelHandler>());
```

The string constructor uses the executable's directory as its working directory. A `Func<ProcessStartInfo>` constructor is also available for arguments and other process settings. `IpcChildProcessHostOptions` configures the pipe name, server options, executable validation, shutdown timeout, and process-tree termination.

The host sets `UseShellExecute` to `false` and places the session's pipe name and authentication key in the new process's environment. There is no command-line secret to log or configuration file to distribute.

The child reconstructs the session and connects:

```csharp
using Liftoff.Ipc;

await using var client = await IpcClient.ConnectFromEnvironmentAsync(
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
- The add-in creates one `IpcChildProcessHost` for each child executable it launches.
- The child executable calls `IpcClient.ConnectFromEnvironmentAsync` and connects as the client.
- Both projects reference a small shared contracts assembly and the build of `Liftoff.Ipc` appropriate for their target framework.

The endpoints may target different supported frameworks. For example, a Revit 2024 add-in can use .NET Framework 4.8 while its child executable uses .NET 8, provided both sides deploy compatible copies of the same contracts assembly.

IPC handlers do not execute on Revit's UI/API thread. A handler that needs the Revit API should marshal the work through Revit's `ExternalEvent` mechanism and complete its task when that work finishes. This keeps Revit-specific scheduling in the add-in and out of the transport library.

Revit can open and close multiple documents during one process lifetime. Choose session ownership deliberately: an application-scoped child can share the add-in's lifetime, while document-specific children should be stopped when their owning document closes.

## Publication and lifecycle

The host exposes both strong and transient publication paths:

```csharp
var delivered = await host.PublishAsync(modelChanged, cancellationToken);
await host.PublishBestEffortAsync(selectionChanged);
```

`PublishAsync` reports lifecycle and publication failures. `PublishBestEffortAsync` is a no-op while stopped and ignores expected disconnect or disposal races, while allowing unexpected serialization and programming errors to propagate.

The default normal-shutdown sequence is:

1. Ask the child to close, or call `Process.CloseMainWindow` for a desktop child.
2. Allow a short grace period with `WaitForExitAsync`.
3. If the child does not exit, terminate it according to the host application's policy.
4. Dispose the server to disconnect any remaining IPC session and release the pipe.

`ChildProcessExited` reports an independently exiting child's process ID and exit code. The IPC server remains running until the host is stopped or restarted, allowing the parent to decide whether an unexpected exit should merely update UI state or trigger recovery.

For policies beyond this standard lifecycle, use `IpcSession`, `IpcServer`, and `IpcClient` directly. Holding a process handle does not make Windows terminate the child if the parent crashes or is forcibly killed. Applications that require crash-coupled lifetime should additionally use a Windows Job Object or another platform-specific supervisor.

### Low-level lifecycle

The underlying session API remains available for custom launchers:

```csharp
var session = IpcSession.Create();
await using var server = IpcServer.Create(session);
server.RegisterHandlersFromAssemblyContaining<AnalyzeModelHandler>();
await server.StartAsync();

var startInfo = new ProcessStartInfo(childExecutablePath);
session.ConfigureChildProcess(startInfo);
using var child = Process.Start(startInfo)
    ?? throw new InvalidOperationException("The child process did not start.");
```

## Deployment

Deploy the child executable and all of its runtime files at a location known to the parent. The parent should resolve that location itself rather than accept an arbitrary executable path from the IPC client.

The WPF demo's server build copies the client output into a `Client` directory under the server output. A production Revit installer can use the same conceptual layout inside the add-in installation directory. The library imposes no required directory structure.

Keep the parent, child, shared contracts, and IPC library versions aligned as one product release. Contract identities are derived from the shared CLR type's assembly and full names, so renaming or moving a contract type is a wire-level change for already deployed endpoints.
