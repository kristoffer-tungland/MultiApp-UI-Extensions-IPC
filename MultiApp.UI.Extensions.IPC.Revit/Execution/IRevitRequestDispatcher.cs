namespace MultiApp.UI.Extensions.IPC.Revit.Execution;

public interface IRevitRequestDispatcher
{
    Task InvokeAsync(Func<CancellationToken, Task> callback, CancellationToken cancellationToken = default);

    Task<TResult?> InvokeAsync<TResult>(Func<CancellationToken, Task<TResult?>> callback, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TItem> InvokeStreamAsync<TItem>(Func<CancellationToken, IAsyncEnumerable<TItem>> callback, CancellationToken cancellationToken = default);
}
