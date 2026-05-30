namespace YourCompany.Ipc.Core.Protocol;

public class IpcPacket
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public string? CorrelationId { get; set; }
    public MessageType Type { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? PayloadType { get; set; }
    public string? PayloadJson { get; set; }
    public bool IsCancelled { get; set; }
}
