using MultiApp.UI.Extensions.IPC.Core.Cqrs;

namespace MultiApp.UI.Extensions.IPC.Core.Host;

public interface IIpcHostEngine : IAsyncDisposable
{
    Task StartClientAsync(HostConfig config, CancellationToken cancellationToken = default);

    Task SendNotificationAsync<T>(string action, T payload, CancellationToken cancellationToken = default);

    Task<TResponse?> SendRequestAsync<TRequest, TResponse>(
        string action,
        TRequest payload,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TItem> SendStreamingRequestAsync<TRequest, TItem>(
        string action,
        TRequest payload,
        CancellationToken cancellationToken = default);

    Task SendCommandAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : IIpcCommand;

    Task<TResponse?> SendQueryAsync<TQuery, TResponse>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IIpcQuery<TResponse>;

    IAsyncEnumerable<TItem> SendStreamQueryAsync<TQuery, TItem>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IIpcStreamQuery<TItem>;

    IAsyncEnumerable<TItem> SendBatchStreamQueryAsync<TQuery, TItem>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IIpcBatchStreamQuery<TItem>;

    Task PublishEventAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIpcEvent;

    Task RegisterCommandHandlerAsync<TCommand>(
        Func<TCommand, CancellationToken, Task> handler,
        string? action = null)
        where TCommand : IIpcCommand;

    Task RegisterQueryHandlerAsync<TQuery, TResponse>(
        Func<TQuery, CancellationToken, Task<TResponse?>> handler,
        string? action = null)
        where TQuery : IIpcQuery<TResponse>;

    Task RegisterStreamQueryHandlerAsync<TQuery, TItem>(
        Func<TQuery, CancellationToken, IAsyncEnumerable<TItem>> handler,
        string? action = null)
        where TQuery : IIpcStreamQuery<TItem>;

    Task RegisterBatchStreamQueryHandlerAsync<TQuery, TItem>(
        Func<TQuery, CancellationToken, IAsyncEnumerable<IReadOnlyList<TItem>>> handler,
        string? action = null)
        where TQuery : IIpcBatchStreamQuery<TItem>;

    Task RegisterEventHandlerAsync<TEvent>(
        Func<TEvent, CancellationToken, Task> handler,
        string? action = null)
        where TEvent : IIpcEvent;

    Task StopAsync(CancellationToken cancellationToken = default);
}
