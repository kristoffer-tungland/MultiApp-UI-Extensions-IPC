using MultiApp.UI.Extensions.IPC.Core.Host;
using MultiApp.UI.Extensions.IPC.Revit.Server;

namespace MultiApp.UI.Extensions.IPC.Revit.Host;

public interface IRevitIpcHostEngine : IIpcHostEngine
{
    RevitIpcServer Server { get; }

    Task NotifyDocumentClosingAsync(string documentId, CancellationToken cancellationToken = default);

    Task NotifyApplicationClosingAsync(CancellationToken cancellationToken = default);
}
