using System.Net.Sockets;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class MultiProtocolDeviceSession
{
    private const int BufferSize = 65536;
    private const int MaxDetectionBytes = 16384;

    private readonly ConnectionManager _connections;
    private readonly ProxyConnection _connection;
    private readonly TcpClient _client;
    private readonly TelemetryStore _telemetryStore;
    private readonly Jt808FrameBuffer _jt808Buffer = new();
    private readonly Gt06FrameBuffer _gt06Buffer = new();
    private readonly List<byte[]> _pendingJt808Frames = new();
    private readonly List<byte[]> _pendingGt06Frames = new();
    private DeviceSession? _jt808Session;
    private Gt06DeviceSession? _gt06Session;
    private DeviceProtocol? _protocol;
    private int _detectionBytes;

    public MultiProtocolDeviceSession(
        ConnectionManager connections,
        ProxyConnection connection,
        TcpClient client,
        TelemetryStore telemetryStore)
    {
        _connections = connections;
        _connection = connection;
        _client = client;
        _telemetryStore = telemetryStore;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await RunWithPrefetchedAsync(_client.GetStream(), ReadOnlyMemory<byte>.Empty, cancellationToken);
    }

    public async Task RunWithPrefetchedAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> prefetched,
        CancellationToken cancellationToken)
    {
        var ownsLifecycle = true;

        try
        {
            if (!prefetched.IsEmpty)
                await ProcessChunkAsync(stream, prefetched, cancellationToken);

            var buffer = new byte[BufferSize];

            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                    break;

                await ProcessChunkAsync(stream, buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException ex)
        {
            TrafficLogger.LogInfo($"IO ошибка сессии #{_connection.ConnectionId}: {ex.Message}");
        }
        catch (SocketException ex)
        {
            TrafficLogger.LogInfo($"Сокет закрыт #{_connection.ConnectionId}: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (ownsLifecycle)
            {
                CloseQuietly(_client);
                _connections.Unregister(_connection);
            }
        }
    }

    private async Task ProcessChunkAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        if (_protocol == null)
        {
            await DetectAndProcessAsync(stream, chunk, cancellationToken);
            return;
        }

        if (_protocol == DeviceProtocol.Gt06)
            await _gt06Session!.ProcessInitialChunkAsync(stream, chunk, cancellationToken);
        else
            await _jt808Session!.ProcessInitialChunkAsync(stream, chunk, cancellationToken);
    }

    private async Task DetectAndProcessAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        _jt808Buffer.Append(chunk.Span);
        _gt06Buffer.Append(chunk.Span);
        _detectionBytes += chunk.Length;

        _pendingJt808Frames.AddRange(_jt808Buffer.ExtractFrames());
        _pendingGt06Frames.AddRange(_gt06Buffer.ExtractFrames());

        var detection = ProtocolDetector.TryDetect(_pendingJt808Frames, _pendingGt06Frames)
            ?? ProtocolDetector.TryDetectProtocolOnly(_pendingJt808Frames, _pendingGt06Frames);

        if (detection == null)
        {
            if (_detectionBytes < MaxDetectionBytes)
                return;

            detection = new ProtocolDetectionResult
            {
                Protocol = ProtocolDetector.GuessProtocol(chunk.Span),
                DeviceId = string.Empty,
                FromRegistry = false
            };
        }

        if (detection.Protocol == DeviceProtocol.Gt06 && _pendingGt06Frames.Count == 0)
            return;

        if (detection.Protocol == DeviceProtocol.Gt23 || DeviceRegistry.ShouldProxyToRemote(detection.DeviceId))
            return;

        ActivateProtocol(detection);

        var deviceLabel = string.IsNullOrWhiteSpace(detection.DeviceId) ? "?" : DeviceRegistry.GetDisplayName(detection.DeviceId);
        TrafficLogger.LogInfo(
            $"[{deviceLabel}] протокол: {DeviceProtocolParser.ToConfigValue(detection.Protocol)}{(detection.FromRegistry ? " (из devices.json)" : " (определён автоматически)")}");

        if (detection.Protocol == DeviceProtocol.Gt06)
        {
            foreach (var frame in _pendingGt06Frames)
                await _gt06Session!.ProcessFrameAsync(stream, frame, cancellationToken);
        }
        else
        {
            foreach (var frame in _pendingJt808Frames)
                await _jt808Session!.ProcessFrameAsync(stream, frame, cancellationToken);
        }

        _pendingGt06Frames.Clear();
        _pendingJt808Frames.Clear();
    }

    private void ActivateProtocol(ProtocolDetectionResult detection)
    {
        _protocol = detection.Protocol;
        _connection.Protocol = detection.Protocol;

        if (!string.IsNullOrWhiteSpace(detection.DeviceId))
            _connection.DeviceId = detection.DeviceId;

        if (detection.Protocol == DeviceProtocol.Gt06)
            _gt06Session = new Gt06DeviceSession(_connections, _connection, _client, _telemetryStore);
        else
            _jt808Session = new DeviceSession(_connections, _connection, _client, _telemetryStore);
    }

    private static void CloseQuietly(TcpClient client)
    {
        try
        {
            client.Close();
        }
        catch (Exception ex)
        {
            TrafficLogger.LogInfo($"Ошибка при закрытии клиента: {ex.Message}");
        }
    }
}
