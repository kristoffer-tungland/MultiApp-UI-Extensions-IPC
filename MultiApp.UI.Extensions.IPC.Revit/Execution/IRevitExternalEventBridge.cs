namespace MultiApp.UI.Extensions.IPC.Revit.Execution;

public interface IRevitExternalEventBridge
{
    Task InvokeAsync(Func<CancellationToken, Task> callback, CancellationToken cancellationToken = default);

    Task<TResult?> InvokeAsync<TResult>(Func<CancellationToken, Task<TResult?>> callback, CancellationToken cancellationToken = default);
}
