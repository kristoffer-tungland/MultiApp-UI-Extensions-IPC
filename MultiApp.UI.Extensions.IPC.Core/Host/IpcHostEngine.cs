using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MultiApp.UI.Extensions.IPC.Core.Cqrs;
using MultiApp.UI.Extensions.IPC.Core.Protocol;
using MultiApp.UI.Extensions.IPC.Core.Serialization;

namespace MultiApp.UI.Extensions.IPC.Core.Host;

public sealed class IpcHostEngine : IIpcHostEngine
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcPacket>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, Channel<IpcPacket>> _pendingStreams = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incomingRequestCancellations = new();
    private readonly ConcurrentDictionary<string, Func<IpcPacket, CancellationToken, Task>> _incomingHandlers = new(StringComparer.Ordinal);

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _clientProcess;
    private Task? _readLoop;
    private CancellationTokenSource? _connectionCts;
    private TimeSpan _gracefulShutdownTimeout = TimeSpan.FromMilliseconds(250);
    private bool _disposed;

    public async Task StartClientAsync(HostConfig config, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();

        ArgumentException.ThrowIfNullOrWhiteSpace(config.ClientExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.PipeName);

        if (config.ConnectionTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config.ConnectionTimeoutMs));
        }

        if (config.GracefulShutdownTimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config.GracefulShutdownTimeoutMs));
        }

        await StopAsync(cancellationToken).ConfigureAwait(false);
        _gracefulShutdownTimeout = TimeSpan.FromMilliseconds(config.GracefulShutdownTimeoutMs);
        _connectionCts = new CancellationTokenSource();

        _pipe = new NamedPipeServerStream(
            config.PipeName,
            PipeDirection.InOut,
            config.IsSingleInstance ? 1 : NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        _clientProcess = StartClientProcess(config);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(config.ConnectionTimeoutMs);
        await _pipe.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);

        _reader = new StreamReader(_pipe);
        _writer = new StreamWriter(_pipe) { AutoFlush = true };

        _readLoop = Task.Run(
            () => ReadLoopAsync(_connectionCts.Token),
            _connectionCts.Token);
    }

    public Task SendNotificationAsync<T>(string action, T payload, CancellationToken cancellationToken = default)
    {
        var packet = CreatePacket(MessageType.Request, action, payload, correlationId: null);
        return SendPacketAsync(packet, cancellationToken);
    }

    public async Task<TResponse?> SendRequestAsync<TRequest, TResponse>(
        string action,
        TRequest payload,
        CancellationToken cancellationToken = default)
    {
        var packet = CreatePacket(MessageType.Request, action, payload, correlationId: null);
        var completion = new TaskCompletionSource<IpcPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(packet.MessageId, completion))
        {
            throw new InvalidOperationException($"Duplicate request id '{packet.MessageId}'.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            if (_pendingRequests.TryRemove(packet.MessageId, out var pending))
            {
                pending.TrySetCanceled(cancellationToken);
                _ = TrySendCancellationSignalAsync(action, packet.MessageId);
            }
        });

        await SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);
        var response = await completion.Task.ConfigureAwait(false);

        if (response.IsCancelled)
        {
            throw new OperationCanceledException($"Request '{packet.MessageId}' was cancelled by remote process.");
        }

        return IpcJsonSerializer.DeserializePayload<TResponse>(response.PayloadJson);
    }

    public async IAsyncEnumerable<TItem> SendStreamingRequestAsync<TRequest, TItem>(
        string action,
        TRequest payload,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var packet = CreatePacket(MessageType.Request, action, payload, correlationId: null);
        var channel = Channel.CreateUnbounded<IpcPacket>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        if (!_pendingStreams.TryAdd(packet.MessageId, channel))
        {
            throw new InvalidOperationException($"Duplicate stream request id '{packet.MessageId}'.");
        }

        using var registration = cancellationToken.Register(() =>
        {
            _ = TrySendCancellationSignalAsync(action, packet.MessageId);
            channel.Writer.TryComplete(new OperationCanceledException(cancellationToken));
            _pendingStreams.TryRemove(packet.MessageId, out _);
        });

        await SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);

        await foreach (var incoming in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (incoming.Type == MessageType.StreamEnd)
            {
                if (incoming.IsCancelled)
                {
                    throw new OperationCanceledException($"Stream '{packet.MessageId}' was cancelled by remote process.");
                }

                yield break;
            }

            if (incoming.IsCancelled)
            {
                throw new OperationCanceledException($"Stream '{packet.MessageId}' was cancelled by remote process.");
            }

            if (incoming.Type == MessageType.StreamBatch)
            {
                var batch = IpcJsonSerializer.DeserializePayload<List<TItem>>(incoming.PayloadJson) ?? [];
                foreach (var item in batch)
                {
                    yield return item;
                }

                continue;
            }

            if (incoming.Type == MessageType.StreamItem)
            {
                var item = IpcJsonSerializer.DeserializePayload<TItem>(incoming.PayloadJson);
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }

    public Task SendCommandAsync<TCommand>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : IIpcCommand
        => SendNotificationAsync(IpcActionNameResolver.For<TCommand>(), command, cancellationToken);

    public Task<TResponse?> SendQueryAsync<TQuery, TResponse>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IIpcQuery<TResponse>
        => SendRequestAsync<TQuery, TResponse>(IpcActionNameResolver.For<TQuery>(), query, cancellationToken);

    public IAsyncEnumerable<TItem> SendStreamQueryAsync<TQuery, TItem>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IIpcStreamQuery<TItem>
        => SendStreamingRequestAsync<TQuery, TItem>(IpcActionNameResolver.For<TQuery>(), query, cancellationToken);

    public Task PublishEventAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIpcEvent
        => SendNotificationAsync(IpcActionNameResolver.For<TEvent>(), @event, cancellationToken);

    public Task RegisterCommandHandlerAsync<TCommand>(
        Func<TCommand, CancellationToken, Task> handler,
        string? action = null)
        where TCommand : IIpcCommand
        => RegisterIncomingHandlerAsync(action ?? IpcActionNameResolver.For<TCommand>(), async (packet, cancellationToken) =>
        {
            var request = DeserializePayload<TCommand>(packet);
            await handler(request, cancellationToken).ConfigureAwait(false);
            await SendResponsePacketAsync(packet, payload: null, payloadType: null, isCancelled: false, cancellationToken).ConfigureAwait(false);
        });

    public Task RegisterQueryHandlerAsync<TQuery, TResponse>(
        Func<TQuery, CancellationToken, Task<TResponse?>> handler,
        string? action = null)
        where TQuery : IIpcQuery<TResponse>
        => RegisterIncomingHandlerAsync(action ?? IpcActionNameResolver.For<TQuery>(), async (packet, cancellationToken) =>
        {
            var query = DeserializePayload<TQuery>(packet);
            var result = await handler(query, cancellationToken).ConfigureAwait(false);
            await SendResponsePacketAsync(packet, result, typeof(TResponse), isCancelled: false, cancellationToken).ConfigureAwait(false);
        });

    public Task RegisterStreamQueryHandlerAsync<TQuery, TItem>(
        Func<TQuery, CancellationToken, IAsyncEnumerable<TItem>> handler,
        string? action = null)
        where TQuery : IIpcStreamQuery<TItem>
        => RegisterIncomingHandlerAsync(action ?? IpcActionNameResolver.For<TQuery>(), async (packet, cancellationToken) =>
        {
            var query = DeserializePayload<TQuery>(packet);
            var stream = handler(query, cancellationToken);

            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await SendPacketAsync(new IpcPacket
                {
                    Type = MessageType.StreamItem,
                    Action = packet.Action,
                    CorrelationId = packet.MessageId,
                    PayloadType = item?.GetType().FullName ?? typeof(TItem).FullName,
                    PayloadJson = IpcJsonSerializer.SerializePayload(item)
                }, cancellationToken).ConfigureAwait(false);
            }

            await SendPacketAsync(new IpcPacket
            {
                Type = MessageType.StreamEnd,
                Action = packet.Action,
                CorrelationId = packet.MessageId
            }, cancellationToken).ConfigureAwait(false);
        });

    public Task RegisterEventHandlerAsync<TEvent>(
        Func<TEvent, CancellationToken, Task> handler,
        string? action = null)
        where TEvent : IIpcEvent
        => RegisterIncomingHandlerAsync(action ?? IpcActionNameResolver.For<TEvent>(), async (packet, cancellationToken) =>
        {
            var @event = DeserializePayload<TEvent>(packet);
            await handler(@event, cancellationToken).ConfigureAwait(false);
        });

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _connectionCts?.Cancel();

        if (_readLoop is not null)
        {
            await Task.WhenAny(_readLoop, Task.Delay(_gracefulShutdownTimeout, cancellationToken)).ConfigureAwait(false);
            _readLoop = null;
        }

        _connectionCts?.Dispose();
        _connectionCts = null;

        if (_clientProcess is { HasExited: false })
        {
            try
            {
                _clientProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceWarning("Client process shutdown race detected: {0}", ex.Message);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Trace.TraceWarning("Client process shutdown failed: {0}", ex.Message);
            }
        }

        _writer?.Dispose();
        _reader?.Dispose();

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        _writer = null;
        _reader = null;
        _pipe = null;
        _clientProcess = null;

        FailPendingOperations(new OperationCanceledException("Host engine stopped."));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _disposed = true;
    }

    private Process StartClientProcess(HostConfig config)
    {
        var pipeArgument = string.IsNullOrWhiteSpace(config.ClientArguments)
            ? $"--pipe-name \"{config.PipeName}\""
            : $"--pipe-name \"{config.PipeName}\" {config.ClientArguments}";

        var startInfo = new ProcessStartInfo
        {
            FileName = config.ClientExecutablePath,
            Arguments = pipeArgument,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                ? Path.GetDirectoryName(config.ClientExecutablePath) ?? Environment.CurrentDirectory
                : config.WorkingDirectory
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start client process: {config.ClientExecutablePath}");
    }

    private static IpcPacket CreatePacket<T>(MessageType type, string action, T payload, string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        return new IpcPacket
        {
            Type = type,
            Action = action,
            CorrelationId = correlationId,
            PayloadType = payload?.GetType().FullName,
            PayloadJson = IpcJsonSerializer.SerializePayload(payload)
        };
    }

    private async Task SendPacketAsync(IpcPacket packet, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("Host is not connected.");
        }

        var line = IpcJsonSerializer.SerializePacket(packet);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException ex)
            {
                FailPendingOperations(ex);
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
            {
                FailPendingOperations(new IOException("IPC pipe disconnected."));
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            IpcPacket packet;
            try
            {
                packet = IpcJsonSerializer.DeserializePacket(line);
            }
            catch (JsonException)
            {
                Trace.TraceWarning("Received malformed IPC packet.");
                continue;
            }

            HandleIncomingPacket(packet);
        }
    }

    private void HandleIncomingPacket(IpcPacket packet)
    {
        if (packet.Type == MessageType.Response &&
            !string.IsNullOrWhiteSpace(packet.CorrelationId) &&
            _pendingRequests.TryRemove(packet.CorrelationId, out var pendingRequest))
        {
            pendingRequest.TrySetResult(packet);
            return;
        }

        if ((packet.Type == MessageType.StreamItem || packet.Type == MessageType.StreamBatch || packet.Type == MessageType.StreamEnd) &&
            !string.IsNullOrWhiteSpace(packet.CorrelationId) &&
            _pendingStreams.TryGetValue(packet.CorrelationId, out var streamChannel))
        {
            streamChannel.Writer.TryWrite(packet);

            if (packet.Type == MessageType.StreamEnd)
            {
                streamChannel.Writer.TryComplete();
                _pendingStreams.TryRemove(packet.CorrelationId, out _);
            }

            return;
        }

        if (packet.Type == MessageType.LifecycleSignal &&
            packet.IsCancelled &&
            !string.IsNullOrWhiteSpace(packet.CorrelationId) &&
            _incomingRequestCancellations.TryRemove(packet.CorrelationId, out var pendingCancellation))
        {
            pendingCancellation.Cancel();
            pendingCancellation.Dispose();
            return;
        }

        if (packet.Type == MessageType.Request)
        {
            if (_incomingHandlers.TryGetValue(packet.Action, out var handler))
            {
                _ = ExecuteIncomingHandlerAsync(packet, handler);
                return;
            }

            _ = SendResponsePacketAsync(packet, payload: null, payloadType: null, isCancelled: true, CancellationToken.None);
        }
    }

    private async Task ExecuteIncomingHandlerAsync(IpcPacket packet, Func<IpcPacket, CancellationToken, Task> handler)
    {
        var handlerCts = new CancellationTokenSource();
        _incomingRequestCancellations[packet.MessageId] = handlerCts;

        try
        {
            await handler(packet, handlerCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendResponsePacketAsync(packet, payload: null, payloadType: null, isCancelled: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceError("Unhandled IPC request handler error: {0}", ex);
            await SendResponsePacketAsync(packet, payload: new { Error = ex.Message }, payloadType: typeof(string), isCancelled: false, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (_incomingRequestCancellations.TryRemove(packet.MessageId, out var current))
            {
                current.Dispose();
            }
        }
    }

    private Task RegisterIncomingHandlerAsync(string action, Func<IpcPacket, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!_incomingHandlers.TryAdd(action, handler))
        {
            throw new InvalidOperationException($"A handler for action '{action}' is already registered.");
        }

        return Task.CompletedTask;
    }

    private static T DeserializePayload<T>(IpcPacket packet)
    {
        var payload = IpcJsonSerializer.DeserializePayload<T>(packet.PayloadJson);
        if (payload is null)
        {
            throw new InvalidOperationException($"Request payload for action '{packet.Action}' cannot be null.");
        }

        return payload;
    }

    private Task SendResponsePacketAsync(
        IpcPacket request,
        object? payload,
        Type? payloadType,
        bool isCancelled,
        CancellationToken cancellationToken)
        => SendPacketAsync(new IpcPacket
        {
            Type = MessageType.Response,
            Action = request.Action,
            CorrelationId = request.MessageId,
            PayloadType = payloadType?.FullName,
            PayloadJson = IpcJsonSerializer.SerializePayload(payload),
            IsCancelled = isCancelled
        }, cancellationToken);

    private Task TrySendCancellationSignalAsync(string action, string correlationId)
        => SendPacketAsync(new IpcPacket
        {
            Type = MessageType.LifecycleSignal,
            Action = action,
            CorrelationId = correlationId,
            IsCancelled = true
        }, CancellationToken.None);

    private void FailPendingOperations(Exception ex)
    {
        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(ex);
        }

        _pendingRequests.Clear();

        foreach (var stream in _pendingStreams.Values)
        {
            stream.Writer.TryComplete(ex);
        }

        _pendingStreams.Clear();

        foreach (var cancellation in _incomingRequestCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _incomingRequestCancellations.Clear();
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
