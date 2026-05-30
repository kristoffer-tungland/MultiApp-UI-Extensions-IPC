using YourCompany.Ipc.Core.Host;

namespace YourCompany.Ipc.Revit.Host;

public interface IRevitIpcHostEngine : IIpcHostEngine
{
    Task NotifyDocumentClosingAsync(string documentId, CancellationToken cancellationToken = default);

    Task NotifyApplicationClosingAsync(CancellationToken cancellationToken = default);
}
