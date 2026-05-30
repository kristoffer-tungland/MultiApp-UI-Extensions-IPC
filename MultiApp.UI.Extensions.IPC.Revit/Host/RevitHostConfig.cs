using MultiApp.UI.Extensions.IPC.Core.Host;

namespace MultiApp.UI.Extensions.IPC.Revit.Host;

public sealed class RevitHostConfig : HostConfig
{
    public nint ParentWindowHandle { get; set; }
    public string? AddInId { get; set; }
}
