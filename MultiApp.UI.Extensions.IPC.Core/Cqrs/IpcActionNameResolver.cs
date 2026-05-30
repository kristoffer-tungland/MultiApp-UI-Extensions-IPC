namespace MultiApp.UI.Extensions.IPC.Core.Cqrs;

public static class IpcActionNameResolver
{
    public static string For<TMessage>() where TMessage : IIpcMessage => For(typeof(TMessage));

    public static string For(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        return messageType.FullName ?? messageType.Name;
    }
}
