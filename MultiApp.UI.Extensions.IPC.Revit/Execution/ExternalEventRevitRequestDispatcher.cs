namespace MultiApp.UI.Extensions.IPC.Revit.Execution;

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
