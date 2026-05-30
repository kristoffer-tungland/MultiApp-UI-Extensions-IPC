using YourCompany.Ipc.Core.Host;

namespace YourCompany.Ipc.Revit.Host;

public sealed class RevitIpcHostEngine : IRevitIpcHostEngine
{
    private readonly IpcHostEngine _engine = new();

    public Task StartClientAsync(HostConfig config, CancellationToken cancellationToken = default)
        => _engine.StartClientAsync(config, cancellationToken);

    public Task SendNotificationAsync<T>(string action, T payload, CancellationToken cancellationToken = default)
        => _engine.SendNotificationAsync(action, payload, cancellationToken);

    public Task<TResponse?> SendRequestAsync<TRequest, TResponse>(string action, TRequest payload, CancellationToken cancellationToken = default)
        => _engine.SendRequestAsync<TRequest, TResponse>(action, payload, cancellationToken);

    public IAsyncEnumerable<TItem> SendStreamingRequestAsync<TRequest, TItem>(string action, TRequest payload, CancellationToken cancellationToken = default)
        => _engine.SendStreamingRequestAsync<TRequest, TItem>(action, payload, cancellationToken);

    public Task NotifyDocumentClosingAsync(string documentId, CancellationToken cancellationToken = default)
        => _engine.SendNotificationAsync("DocumentClosing", new { DocumentId = documentId }, cancellationToken);

    public Task NotifyApplicationClosingAsync(CancellationToken cancellationToken = default)
        => _engine.SendNotificationAsync("ApplicationClosing", new { UtcTimestamp = DateTimeOffset.UtcNow }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _engine.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
