using MultiApp.UI.Extensions.IPC.Core.Cqrs;
using MultiApp.UI.Extensions.IPC.Revit.Server;

namespace MultiApp.UI.Extensions.IPC.Revit.Host;

public sealed class RevitIpcRuntime : IAsyncDisposable
{
    public RevitIpcRuntime(IRevitIpcHostEngine hostEngine)
    {
        HostEngine = hostEngine ?? throw new ArgumentNullException(nameof(hostEngine));
    }

    public IRevitIpcHostEngine HostEngine { get; }

    public RevitIpcServer Server => HostEngine.Server;

    public Task StartClientAsync(RevitHostConfig config, CancellationToken cancellationToken = default)
        => HostEngine.StartClientAsync(config, cancellationToken);

    public Task SendCommandAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : IIpcCommand
        => HostEngine.SendCommandAsync(command, cancellationToken);

    public Task<TResponse?> SendQueryAsync<TQuery, TResponse>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IIpcQuery<TResponse>
        => HostEngine.SendQueryAsync<TQuery, TResponse>(query, cancellationToken);

    public IAsyncEnumerable<TItem> SendStreamQueryAsync<TQuery, TItem>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IIpcStreamQuery<TItem>
        => HostEngine.SendStreamQueryAsync<TQuery, TItem>(query, cancellationToken);

    public Task PublishEventAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIpcEvent
        => HostEngine.PublishEventAsync(@event, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => HostEngine.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => HostEngine.DisposeAsync();
}
