using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using YourCompany.Ipc.Core.Protocol;
using YourCompany.Ipc.Core.Serialization;

namespace YourCompany.Ipc.Core.Host;

public sealed class IpcHostEngine : IIpcHostEngine
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcPacket>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, Channel<IpcPacket>> _pendingStreams = new();
    private readonly CancellationTokenSource _shutdown = new();

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _clientProcess;
    private Task? _readLoop;

    public async Task StartClientAsync(HostConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config.ClientExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.PipeName);

        await StopAsync(cancellationToken).ConfigureAwait(false);

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
        _readLoop = Task.Run(() => ReadLoopAsync(_shutdown.Token), _shutdown.Token);
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

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

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
            _ = SendPacketAsync(new IpcPacket
            {
                Type = MessageType.LifecycleSignal,
                Action = action,
                CorrelationId = packet.MessageId,
                IsCancelled = true
            }, CancellationToken.None);
            channel.Writer.TryComplete(new OperationCanceledException(cancellationToken));
            _pendingStreams.TryRemove(packet.MessageId, out _);
        });

        await SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);

        await foreach (var incoming in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (incoming.Type == MessageType.StreamEnd)
            {
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

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _shutdown.Cancel();

        if (_readLoop is not null)
        {
            await Task.WhenAny(_readLoop, Task.Delay(250, cancellationToken)).ConfigureAwait(false);
            _readLoop = null;
        }

        if (_clientProcess is { HasExited: false })
        {
            _clientProcess.Kill(entireProcessTree: true);
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

        foreach (var pending in _pendingRequests.Values)
        {
            pending.TrySetException(new OperationCanceledException("Host engine stopped."));
        }

        _pendingRequests.Clear();

        foreach (var stream in _pendingStreams.Values)
        {
            stream.Writer.TryComplete(new OperationCanceledException("Host engine stopped."));
        }

        _pendingStreams.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _writeLock.Dispose();
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
            ?? throw new InvalidOperationException("Failed to start client process.");
    }

    private IpcPacket CreatePacket<T>(MessageType type, string action, T payload, string? correlationId)
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
            await _writer.WriteLineAsync(line).ConfigureAwait(false);
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
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            IpcPacket packet;
            try
            {
                packet = IpcJsonSerializer.DeserializePacket(line);
            }
            catch
            {
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
        }
    }
}
