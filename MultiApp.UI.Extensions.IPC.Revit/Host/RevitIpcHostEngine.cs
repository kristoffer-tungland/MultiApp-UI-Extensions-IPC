using MultiApp.UI.Extensions.IPC.Core.Host;
using MultiApp.UI.Extensions.IPC.Revit.Execution;
using MultiApp.UI.Extensions.IPC.Revit.Server;

namespace MultiApp.UI.Extensions.IPC.Revit.Host;

public sealed class RevitIpcHostEngine : IRevitIpcHostEngine
{
    private readonly IpcHostEngine _engine = new();

    public RevitIpcHostEngine(IRevitExternalEventBridge? externalEventBridge = null)
    {
        var externalDispatcher = externalEventBridge is null
            ? null
            : new ExternalEventRevitRequestDispatcher(externalEventBridge);

        Server = new RevitIpcServer(_engine, externalEventDispatcher: externalDispatcher);
    }

    public RevitIpcServer Server { get; }

    public Task StartClientAsync(HostConfig config, CancellationToken cancellationToken = default)
        => _engine.StartClientAsync(config, cancellationToken);

    public Task SendNotificationAsync<T>(string action, T payload, CancellationToken cancellationToken = default)
        => _engine.SendNotificationAsync(action, payload, cancellationToken);

    public Task<TResponse?> SendRequestAsync<TRequest, TResponse>(string action, TRequest payload, CancellationToken cancellationToken = default)
        => _engine.SendRequestAsync<TRequest, TResponse>(action, payload, cancellationToken);

    public IAsyncEnumerable<TItem> SendStreamingRequestAsync<TRequest, TItem>(string action, TRequest payload, CancellationToken cancellationToken = default)
        => _engine.SendStreamingRequestAsync<TRequest, TItem>(action, payload, cancellationToken);

    public Task SendCommandAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcCommand
        => _engine.SendCommandAsync(command, cancellationToken);

    public Task<TResponse?> SendQueryAsync<TQuery, TResponse>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcQuery<TResponse>
        => _engine.SendQueryAsync<TQuery, TResponse>(query, cancellationToken);

    public IAsyncEnumerable<TItem> SendStreamQueryAsync<TQuery, TItem>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcStreamQuery<TItem>
        => _engine.SendStreamQueryAsync<TQuery, TItem>(query, cancellationToken);

    public Task PublishEventAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcEvent
        => _engine.PublishEventAsync(@event, cancellationToken);

    public Task RegisterCommandHandlerAsync<TCommand>(Func<TCommand, CancellationToken, Task> handler, string? action = null)
        where TCommand : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcCommand
        => _engine.RegisterCommandHandlerAsync(handler, action);

    public Task RegisterQueryHandlerAsync<TQuery, TResponse>(Func<TQuery, CancellationToken, Task<TResponse?>> handler, string? action = null)
        where TQuery : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcQuery<TResponse>
        => _engine.RegisterQueryHandlerAsync<TQuery, TResponse>(handler, action);

    public Task RegisterStreamQueryHandlerAsync<TQuery, TItem>(Func<TQuery, CancellationToken, IAsyncEnumerable<TItem>> handler, string? action = null)
        where TQuery : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcStreamQuery<TItem>
        => _engine.RegisterStreamQueryHandlerAsync<TQuery, TItem>(handler, action);

    public Task RegisterEventHandlerAsync<TEvent>(Func<TEvent, CancellationToken, Task> handler, string? action = null)
        where TEvent : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcEvent
        => _engine.RegisterEventHandlerAsync(handler, action);

    public Task NotifyDocumentClosingAsync(string documentId, CancellationToken cancellationToken = default)
        => _engine.PublishEventAsync(new DocumentClosingEvent(documentId), cancellationToken);

    public Task NotifyApplicationClosingAsync(CancellationToken cancellationToken = default)
        => _engine.PublishEventAsync(new ApplicationClosingEvent(DateTimeOffset.UtcNow), cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _engine.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _engine.DisposeAsync();

    private sealed record DocumentClosingEvent(string DocumentId) : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcEvent;

    private sealed record ApplicationClosingEvent(DateTimeOffset UtcTimestamp) : MultiApp.UI.Extensions.IPC.Core.Cqrs.IIpcEvent;
}
