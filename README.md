# MultiApp.UI.Extensions.IPC

A lightweight, named-pipe IPC library for hosting a UI Extension process from within a desktop application (e.g. a Revit add-in). Communication is bidirectional and follows a CQRS pattern: **Commands**, **Queries**, **Stream Queries**, and **Events**.

---

## Overview

```
┌──────────────────────────────────┐        Named Pipe         ┌────────────────────────────────┐
│   Host Process (e.g. Revit)      │ ◄────────────────────── ► │  Client Process (UI Extension) │
│                                  │                            │                                │
│  IpcHostEngine / RevitIpcHostEngine                           │  IpcHostEngine                 │
│  - starts + owns the pipe server │                            │  - connects to pipe server     │
│  - registers incoming handlers   │                            │  - registers incoming handlers │
│  - sends commands / queries      │                            │  - sends commands / queries    │
└──────────────────────────────────┘                            └────────────────────────────────┘
```

The **host** side creates a `NamedPipeServerStream`, launches the client executable, and waits for it to connect. Either side can then send messages to the other at any time.

---

## Projects

| Project | Description |
|---|---|
| `MultiApp.UI.Extensions.IPC.Core` | Framework-agnostic host engine, CQRS contracts, protocol, and serialization |
| `MultiApp.UI.Extensions.IPC.Revit` | Revit-specific wrapper that integrates with Revit's `IExternalEvent` threading model |

---

## Getting Started

### 1. Define your message contracts

All messages implement one of the four contract interfaces from `MultiApp.UI.Extensions.IPC.Core.Cqrs`:

```csharp
using MultiApp.UI.Extensions.IPC.Core.Cqrs;

// Fire-and-forget command (no response)
public record OpenDocumentCommand(string FilePath) : IIpcCommand;

// Request / response query
public record GetActiveDocumentQuery() : IIpcQuery<DocumentDto>;

// Streaming query – yields multiple items
public record GetElementsQuery(string CategoryName) : IIpcStreamQuery<ElementDto>;

// Event published by either side
public record DocumentOpenedEvent(string DocumentId, string Title) : IIpcEvent;

// Supporting DTOs
public record DocumentDto(string Id, string Title);
public record ElementDto(int Id, string Name);
```

Action names are derived automatically from the type's `FullName`, so no manual registration strings are needed.

---

### 2. Host side (e.g. a Revit add-in)

#### Generic host (no Revit dependency)

```csharp
using MultiApp.UI.Extensions.IPC.Core.Host;

// 1. Create the engine
await using var engine = new IpcHostEngine();

// 2. Register handlers before starting the client
await engine.RegisterCommandHandlerAsync<OpenDocumentCommand>(async (cmd, ct) =>
{
    // Handle the command – open the document, etc.
    Console.WriteLine($"Opening: {cmd.FilePath}");
});

await engine.RegisterQueryHandlerAsync<GetActiveDocumentQuery, DocumentDto>(async (query, ct) =>
{
    return new DocumentDto("doc-1", "My Project.rvt");
});

await engine.RegisterStreamQueryHandlerAsync<GetElementsQuery, ElementDto>(async (query, ct) =>
{
    // Return an async stream of elements
    return GetElementsAsync(query.CategoryName, ct);
});

await engine.RegisterEventHandlerAsync<DocumentOpenedEvent>(async (evt, ct) =>
{
    Console.WriteLine($"Document opened on the client: {evt.Title}");
});

// 3. Start the client process and wait for it to connect
var config = new HostConfig
{
    PipeName            = "my-app-ipc",
    ClientExecutablePath = @"C:\Path\To\UIExtension.exe",
    ClientArguments     = "--pipe my-app-ipc",
    ConnectionTimeoutMs = 10_000,
};
await engine.StartClientAsync(config);

// 4. Send a command / query to the client
await engine.SendCommandAsync(new OpenDocumentCommand(@"C:\projects\sample.rvt"));

var doc = await engine.SendQueryAsync<GetActiveDocumentQuery, DocumentDto>(new GetActiveDocumentQuery());
Console.WriteLine($"Client's active document: {doc?.Title}");

// 5. Dispose when done (sends a graceful shutdown signal to the client)
```

#### Helper method used above

```csharp
private static async IAsyncEnumerable<ElementDto> GetElementsAsync(
    string category,
    [EnumeratorCancellation] CancellationToken ct)
{
    // Simulate yielding elements from Revit's model
    foreach (var element in GetRevitElements(category))
    {
        ct.ThrowIfCancellationRequested();
        yield return new ElementDto(element.Id, element.Name);
    }
}
```

---

### 3. Client side (UI Extension process)

The client uses the same `IpcHostEngine` but connects as the *client* end of the pipe. The easiest approach is to create a second `IpcHostEngine` that acts as a pipe client. See the note in [Architecture](#architecture) — both sides share the same API surface.

> **Note:** In the current design the `IpcHostEngine` owns the pipe *server*. The UI Extension process connects to it. The host's `StartClientAsync` launches the process and waits for the pipe connection. The client process itself uses a `NamedPipeClientStream` to connect; you can wrap this in a thin helper or adapt the engine to your framework's DI container.

```csharp
// UIExtension Program.cs / startup
// Parse pipe name from args: --pipe my-app-ipc
var pipeName = args.GetPipeName();

await using var clientEngine = new IpcHostEngine();   // reused on the client side

// Register handlers for messages coming from the host
await clientEngine.RegisterEventHandlerAsync<DocumentOpenedEvent>(async (evt, ct) =>
{
    // Update UI, etc.
    UpdateDocumentTab(evt.DocumentId, evt.Title);
});

// Connect to the host's named pipe
// (implement ConnectToHostAsync using NamedPipeClientStream internally)
await clientEngine.ConnectToHostAsync(pipeName);

// Query the host
var doc = await clientEngine.SendQueryAsync<GetActiveDocumentQuery, DocumentDto>(
    new GetActiveDocumentQuery());
```

---

### 4. Revit-specific usage (`MultiApp.UI.Extensions.IPC.Revit`)

Revit requires API calls to run on the *API thread* via `IExternalEvent`. The Revit package wraps this transparently.

#### Implement `IRevitExternalEventBridge`

```csharp
using Autodesk.Revit.UI;
using MultiApp.UI.Extensions.IPC.Revit.Execution;

public sealed class RevitExternalEventBridge : IRevitExternalEventBridge
{
    // Internal handler used by the ExternalEvent
    private Func<CancellationToken, Task>? _pendingCallback;
    private TaskCompletionSource? _tcs;

    private readonly ExternalEvent _externalEvent;

    public RevitExternalEventBridge()
    {
        _externalEvent = ExternalEvent.Create(new Handler(this));
    }

    public async Task InvokeAsync(Func<CancellationToken, Task> callback, CancellationToken ct)
    {
        _pendingCallback = callback;
        _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _externalEvent.Raise();
        await _tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<TResult?> InvokeAsync<TResult>(
        Func<CancellationToken, Task<TResult?>> callback,
        CancellationToken ct)
    {
        TResult? result = default;
        await InvokeAsync(async token =>
        {
            result = await callback(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return result;
    }

    private sealed class Handler(RevitExternalEventBridge bridge) : IExternalEventHandler
    {
        public string GetName() => "IPC Bridge";

        public void Execute(UIApplication app)
        {
            if (bridge._pendingCallback is null) return;

            bridge._pendingCallback(CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        bridge._tcs?.TrySetException(t.Exception!.InnerExceptions);
                    else
                        bridge._tcs?.TrySetResult();
                }, TaskScheduler.Default);

            bridge._pendingCallback = null;
        }
    }
}
```

#### Wire up `RevitIpcHostEngine`

```csharp
using MultiApp.UI.Extensions.IPC.Revit.Host;

// In your Revit IExternalApplication.OnStartup
public Result OnStartup(UIControlledApplication application)
{
    var bridge = new RevitExternalEventBridge();

    _runtime = new RevitIpcRuntime(new RevitIpcHostEngine(bridge));
    _ = StartIpcAsync(_runtime, application);

    return Result.Succeeded;
}

private async Task StartIpcAsync(RevitIpcRuntime runtime, UIControlledApplication app)
{
    // Register handlers via RevitIpcServer (wraps with ExternalEvent dispatch automatically)
    await runtime.Server.RegisterCommandHandlerAsync<OpenDocumentCommand>(
        async (cmd, ct) =>
        {
            // This runs on the Revit API thread via ExternalEvent
            UIApplication uiApp = GetCurrentUiApp();
            uiApp.OpenAndActivateDocument(cmd.FilePath);
        },
        useExternalEvent: true);  // default – routes through IExternalEvent

    await runtime.Server.RegisterQueryHandlerAsync<GetActiveDocumentQuery, DocumentDto>(
        async (query, ct) =>
        {
            var doc = GetCurrentUiApp().ActiveUIDocument?.Document;
            return doc is null ? null : new DocumentDto(doc.UniqueId, doc.Title);
        },
        useExternalEvent: true);

    await runtime.Server.RegisterStreamQueryHandlerAsync<GetElementsQuery, ElementDto>(
        async (query, ct) => GetElementStreamAsync(query, ct),
        useExternalEvent: true);

    // Subscribe to events coming from the UI Extension
    await runtime.Server.RegisterEventHandlerAsync<DocumentOpenedEvent>(
        async (evt, ct) => { /* update add-in state */ },
        useExternalEvent: false);  // no Revit API needed – run directly

    var config = new RevitHostConfig
    {
        PipeName             = "revit-ui-ext",
        ClientExecutablePath = @"C:\Extensions\MyUIExtension.exe",
        ClientArguments      = "--pipe revit-ui-ext",
        ConnectionTimeoutMs  = 15_000,
        ParentWindowHandle   = app.MainWindowHandle,
        AddInId              = "MY-ADDIN-GUID",
    };

    await runtime.StartClientAsync(config);
}
```

#### Lifecycle notifications

`RevitIpcHostEngine` exposes built-in helpers to notify the client when Revit is closing:

```csharp
// When the active document is about to close:
await _runtime.HostEngine.NotifyDocumentClosingAsync(document.UniqueId);

// When Revit itself is shutting down (IExternalApplication.OnShutdown):
await _runtime.HostEngine.NotifyApplicationClosingAsync();

// Full teardown
await _runtime.StopAsync();
await _runtime.DisposeAsync();
```

---

## CQRS Pattern Reference

| Pattern | Interface | Direction | Response |
|---|---|---|---|
| **Command** | `IIpcCommand` | Host ↔ Client | None (fire-and-forget) |
| **Query** | `IIpcQuery<TResponse>` | Host ↔ Client | Single `TResponse` |
| **Stream Query** | `IIpcStreamQuery<TItem>` | Host ↔ Client | `IAsyncEnumerable<TItem>` |
| **Event** | `IIpcEvent` | Host ↔ Client | None (pub/sub) |

### Commands

```csharp
// Sender
await engine.SendCommandAsync(new OpenDocumentCommand("/path/to/file.rvt"));

// Receiver
await engine.RegisterCommandHandlerAsync<OpenDocumentCommand>(async (cmd, ct) =>
{
    // Process the command; no return value
});
```

### Queries

```csharp
// Sender
var result = await engine.SendQueryAsync<GetActiveDocumentQuery, DocumentDto>(
    new GetActiveDocumentQuery());

// Receiver
await engine.RegisterQueryHandlerAsync<GetActiveDocumentQuery, DocumentDto>(
    async (query, ct) => new DocumentDto("doc-1", "Sample.rvt"));
```

### Stream Queries

```csharp
// Sender
await foreach (var element in engine.SendStreamQueryAsync<GetElementsQuery, ElementDto>(
    new GetElementsQuery("Walls"), cancellationToken))
{
    Console.WriteLine($"{element.Id}: {element.Name}");
}

// Receiver
await engine.RegisterStreamQueryHandlerAsync<GetElementsQuery, ElementDto>(
    (query, ct) => YieldElementsAsync(query.CategoryName, ct));
```

### Events

```csharp
// Publisher
await engine.PublishEventAsync(new DocumentOpenedEvent("doc-42", "Tower.rvt"));

// Subscriber
await engine.RegisterEventHandlerAsync<DocumentOpenedEvent>(async (evt, ct) =>
{
    Console.WriteLine($"Opened: {evt.Title}");
});
```

---

## Configuration

### `HostConfig` (Core)

| Property | Default | Description |
|---|---|---|
| `PipeName` | `""` | Named pipe identifier shared by host and client |
| `ClientExecutablePath` | `""` | Path to the client executable the host will launch |
| `ClientArguments` | `""` | Command-line arguments forwarded to the client |
| `WorkingDirectory` | `null` | Working directory for the client process |
| `IsSingleInstance` | `true` | When `true`, only one client connection is allowed |
| `ConnectionTimeoutMs` | `5000` | Milliseconds to wait for the client to connect |
| `GracefulShutdownTimeoutMs` | `250` | Milliseconds to wait for the client to exit cleanly |

### `RevitHostConfig` (Revit)

Extends `HostConfig` with:

| Property | Description |
|---|---|
| `ParentWindowHandle` | `nint` handle of Revit's main window (for child-window positioning) |
| `AddInId` | GUID string of the Revit add-in |

---

## Architecture

```
MultiApp.UI.Extensions.IPC.Core
├── Cqrs/
│   ├── IIpcMessage            – marker interface
│   ├── IIpcCommand            – fire-and-forget
│   ├── IIpcQuery<TResponse>   – request / response
│   ├── IIpcStreamQuery<TItem> – streaming response
│   ├── IIpcEvent              – publish / subscribe
│   └── IpcActionNameResolver  – type → action string mapping
├── Host/
│   ├── HostConfig             – connection settings
│   ├── IIpcHostEngine         – public API surface
│   └── IpcHostEngine          – named-pipe implementation
├── Protocol/
│   ├── IpcPacket              – wire message envelope
│   └── MessageType            – Request / Response / StreamItem / StreamBatch / StreamEnd / LifecycleSignal
└── Serialization/
    └── IpcJsonSerializer      – System.Text.Json helpers

MultiApp.UI.Extensions.IPC.Revit
├── Execution/
│   ├── IRevitExternalEventBridge        – abstraction over IExternalEvent
│   ├── IRevitRequestDispatcher          – unified dispatcher interface
│   ├── DirectRevitRequestDispatcher     – calls handler inline
│   └── ExternalEventRevitRequestDispatcher – marshals to API thread
├── Host/
│   ├── RevitHostConfig        – adds ParentWindowHandle + AddInId
│   ├── IRevitIpcHostEngine    – extends IIpcHostEngine with lifecycle helpers
│   ├── RevitIpcHostEngine     – full Revit-aware engine
│   └── RevitIpcRuntime        – convenience wrapper
└── Server/
    └── RevitIpcServer         – handler registration with dispatcher routing
```

---

## Threading Notes (Revit)

- **`useExternalEvent: true`** (default on `RevitIpcServer`): the handler callback is marshalled to Revit's API thread via `IExternalEvent`. For stream handlers, the entire stream is materialised inside one event invocation before being replayed to the caller.
- **`useExternalEvent: false`**: the handler runs directly on the thread-pool. Use this for handlers that do not need Revit API access.

---

## Building

```bash
dotnet build MultiApp.UI.Extensions.IPC.slnx
dotnet test  MultiApp.UI.Extensions.IPC.slnx
```
