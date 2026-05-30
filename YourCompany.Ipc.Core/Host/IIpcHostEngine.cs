namespace YourCompany.Ipc.Core.Host;

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

    Task StopAsync(CancellationToken cancellationToken = default);
}
