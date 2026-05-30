using YourCompany.Ipc.Core.Host;

namespace YourCompany.Ipc.Revit.Host;

public sealed class RevitHostConfig : HostConfig
{
    public nint ParentWindowHandle { get; set; }
    public string? AddInId { get; set; }
}
