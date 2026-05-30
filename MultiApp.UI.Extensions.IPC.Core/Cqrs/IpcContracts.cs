namespace MultiApp.UI.Extensions.IPC.Core.Cqrs;

public interface IIpcMessage;

public interface IIpcCommand : IIpcMessage;

public interface IIpcQuery<TResponse> : IIpcMessage;

public interface IIpcStreamQuery<TItem> : IIpcMessage;

public interface IIpcEvent : IIpcMessage;
