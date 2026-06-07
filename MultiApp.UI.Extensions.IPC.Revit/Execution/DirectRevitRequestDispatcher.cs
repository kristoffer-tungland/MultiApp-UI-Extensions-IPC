namespace MultiApp.UI.Extensions.IPC.Revit.Execution;

public sealed class DirectRevitRequestDispatcher : IRevitRequestDispatcher
{
    public Task InvokeAsync(Func<CancellationToken, Task> callback, CancellationToken cancellationToken = default)
        => callback(cancellationToken);

    public Task<TResult?> InvokeAsync<TResult>(Func<CancellationToken, Task<TResult?>> callback, CancellationToken cancellationToken = default)
        => callback(cancellationToken);

    public IAsyncEnumerable<TItem> InvokeStreamAsync<TItem>(Func<CancellationToken, IAsyncEnumerable<TItem>> callback, CancellationToken cancellationToken = default)
        => callback(cancellationToken);
}
