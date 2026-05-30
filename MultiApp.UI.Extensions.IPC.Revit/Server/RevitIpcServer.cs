using MultiApp.UI.Extensions.IPC.Core.Cqrs;
using MultiApp.UI.Extensions.IPC.Core.Host;
using MultiApp.UI.Extensions.IPC.Revit.Execution;

namespace MultiApp.UI.Extensions.IPC.Revit.Server;

public sealed class RevitIpcServer
{
    private readonly IIpcHostEngine _hostEngine;
    private readonly IRevitRequestDispatcher _directDispatcher;
    private readonly IRevitRequestDispatcher? _externalEventDispatcher;

    public RevitIpcServer(
        IIpcHostEngine hostEngine,
        IRevitRequestDispatcher? directDispatcher = null,
        IRevitRequestDispatcher? externalEventDispatcher = null)
    {
        _hostEngine = hostEngine ?? throw new ArgumentNullException(nameof(hostEngine));
        _externalEventDispatcher = externalEventDispatcher;
        _directDispatcher = directDispatcher ?? new DirectRevitRequestDispatcher();
    }

    public Task RegisterCommandHandlerAsync<TCommand>(
        Func<TCommand, CancellationToken, Task> handler,
        bool useExternalEvent = true,
        string? action = null)
        where TCommand : IIpcCommand
    {
        var dispatcher = ResolveDispatcher(useExternalEvent);
        return _hostEngine.RegisterCommandHandlerAsync<TCommand>(
            (command, cancellationToken) => dispatcher.InvokeAsync(token => handler(command, token), cancellationToken),
            action);
    }

    public Task RegisterQueryHandlerAsync<TQuery, TResponse>(
        Func<TQuery, CancellationToken, Task<TResponse?>> handler,
        bool useExternalEvent = true,
        string? action = null)
        where TQuery : IIpcQuery<TResponse>
    {
        var dispatcher = ResolveDispatcher(useExternalEvent);
        return _hostEngine.RegisterQueryHandlerAsync<TQuery, TResponse>(
            (query, cancellationToken) => dispatcher.InvokeAsync(token => handler(query, token), cancellationToken),
            action);
    }

    public Task RegisterStreamQueryHandlerAsync<TQuery, TItem>(
        Func<TQuery, CancellationToken, IAsyncEnumerable<TItem>> handler,
        bool useExternalEvent = true,
        string? action = null)
        where TQuery : IIpcStreamQuery<TItem>
    {
        var dispatcher = ResolveDispatcher(useExternalEvent);
        return _hostEngine.RegisterStreamQueryHandlerAsync<TQuery, TItem>(
            (query, cancellationToken) => dispatcher.InvokeStreamAsync(token => handler(query, token), cancellationToken),
            action);
    }

    public Task RegisterBatchStreamQueryHandlerAsync<TQuery, TItem>(
        Func<TQuery, CancellationToken, IAsyncEnumerable<IReadOnlyList<TItem>>> handler,
        bool useExternalEvent = true,
        string? action = null)
        where TQuery : IIpcBatchStreamQuery<TItem>
    {
        var dispatcher = ResolveDispatcher(useExternalEvent);
        return _hostEngine.RegisterBatchStreamQueryHandlerAsync<TQuery, TItem>(
            (query, cancellationToken) => dispatcher.InvokeStreamAsync<IReadOnlyList<TItem>>(token => handler(query, token), cancellationToken),
            action);
    }

    public Task RegisterEventHandlerAsync<TEvent>(
        Func<TEvent, CancellationToken, Task> handler,
        bool useExternalEvent = true,
        string? action = null)
        where TEvent : IIpcEvent
    {
        var dispatcher = ResolveDispatcher(useExternalEvent);
        return _hostEngine.RegisterEventHandlerAsync<TEvent>(
            (@event, cancellationToken) => dispatcher.InvokeAsync(token => handler(@event, token), cancellationToken),
            action);
    }

    private IRevitRequestDispatcher ResolveDispatcher(bool useExternalEvent)
    {
        if (!useExternalEvent)
        {
            return _directDispatcher;
        }

        return _externalEventDispatcher ?? _directDispatcher;
    }
}
