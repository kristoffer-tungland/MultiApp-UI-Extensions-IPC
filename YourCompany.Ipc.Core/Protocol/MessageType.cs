namespace YourCompany.Ipc.Core.Protocol;

public enum MessageType
{
    Request = 1,
    Response = 2,
    StreamItem = 3,
    StreamBatch = 4,
    StreamEnd = 5,
    LifecycleSignal = 6
}
