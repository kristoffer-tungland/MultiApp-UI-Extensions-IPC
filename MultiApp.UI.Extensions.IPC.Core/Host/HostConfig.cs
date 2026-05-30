namespace MultiApp.UI.Extensions.IPC.Core.Host;

public class HostConfig
{
    public string ClientExecutablePath { get; set; } = string.Empty;
    public string PipeName { get; set; } = string.Empty;
    public bool IsSingleInstance { get; set; } = true;
    public int ConnectionTimeoutMs { get; set; } = 5000;
    public int GracefulShutdownTimeoutMs { get; set; } = 250;
    public string ClientArguments { get; set; } = string.Empty;
    public string? WorkingDirectory { get; set; }
}
