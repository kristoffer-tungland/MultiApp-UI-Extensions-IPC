namespace MultiApp.UI.Extensions.IPC.Revit.Execution;

/// <summary>
/// ExternalEvent dispatcher that executes Revit API work on the ExternalEvent thread.
/// Stream callbacks are materialized before replaying to the caller.
/// </summary>
public sealed class ExternalEventRevitRequestDispatcher : IRevitRequestDispatcher
{
    private readonly IRevitExternalEventBridge _externalEventBridge;

    public ExternalEventRevitRequestDispatcher(IRevitExternalEventBridge externalEventBridge)
    {
        _externalEventBridge = externalEventBridge ?? throw new ArgumentNullException(nameof(externalEventBridge));
    }

    public Task InvokeAsync(Func<CancellationToken, Task> callback, CancellationToken cancellationToken = default)
        => _externalEventBridge.InvokeAsync(callback, cancellationToken);

    public Task<TResult?> InvokeAsync<TResult>(Func<CancellationToken, Task<TResult?>> callback, CancellationToken cancellationToken = default)
        => _externalEventBridge.InvokeAsync(callback, cancellationToken);

    public async IAsyncEnumerable<TItem> InvokeStreamAsync<TItem>(
        Func<CancellationToken, IAsyncEnumerable<TItem>> callback,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Revit ExternalEvent executes on the API thread; this bridge currently materializes the stream
        // inside a single invocation and replays items to preserve thread affinity guarantees.
        var items = await _externalEventBridge.InvokeAsync(async innerToken =>
        {
            var result = new List<TItem>();
            await foreach (var item in callback(innerToken).WithCancellation(innerToken).ConfigureAwait(false))
            {
                result.Add(item);
            }

            return result;
        }, cancellationToken).ConfigureAwait(false) ?? [];

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }
}
