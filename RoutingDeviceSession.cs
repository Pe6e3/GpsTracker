using System.Net.Sockets;
using System.Runtime.InteropServices;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class RoutingDeviceSession
{
    private const int BufferSize = 65536;
    private const int MaxDetectionBytes = 16384;

    private readonly ConnectionManager _connections;
    private readonly ProxyConnection _connection;
    private readonly TcpClient _client;
    private readonly TelemetryStore _telemetryStore;

    public RoutingDeviceSession(
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
        var handedOff = false;

        try
        {
            using var stream = _client.GetStream();
            var prefetched = new List<byte>();
            var jt808Buffer = new Jt808FrameBuffer();
            var gt06Buffer = new Gt06FrameBuffer();
            var hqBuffer = new HqFrameBuffer();
            var pendingJt808Frames = new List<byte[]>();
            var pendingGt06Frames = new List<byte[]>();
            var pendingHqFrames = new List<byte[]>();
            var detectionBytes = 0;
            ProtocolDetectionResult? detection = null;
            var buffer = new byte[BufferSize];

            while (!cancellationToken.IsCancellationRequested)
            {
                detection = DetectProtocol(
                    pendingJt808Frames,
                    pendingGt06Frames,
                    pendingHqFrames,
                    detectionBytes,
                    prefetched);

                if (detection != null && ShouldProxy(detection))
                {
                    handedOff = true;
                    await RunProxySessionAsync(stream, prefetched, detection, cancellationToken);
                    return;
                }

                if (detection != null && DeviceProtocolParser.IsHqProtocol(detection.Protocol))
                {
                    handedOff = true;
                    await RunHqSessionAsync(stream, prefetched, detection, cancellationToken);
                    return;
                }

                if (detection != null && DeviceProtocolParser.IsLocalTcpProtocol(detection.Protocol))
                {
                    handedOff = true;
                    await RunLocalSessionAsync(stream, prefetched, cancellationToken);
                    return;
                }

                if (detectionBytes >= MaxDetectionBytes)
                {
                    handedOff = true;
                    await RunLocalSessionAsync(stream, prefetched, cancellationToken);
                    return;
                }

                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                    return;

                prefetched.AddRange(buffer.AsSpan(0, bytesRead).ToArray());
                detectionBytes += bytesRead;

                jt808Buffer.Append(buffer.AsSpan(0, bytesRead));
                gt06Buffer.Append(buffer.AsSpan(0, bytesRead));
                hqBuffer.Append(buffer.AsSpan(0, bytesRead));
                pendingJt808Frames.AddRange(jt808Buffer.ExtractFrames());
                pendingGt06Frames.AddRange(gt06Buffer.ExtractFrames());
                pendingHqFrames.AddRange(hqBuffer.ExtractFrames());
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
            if (!handedOff)
            {
                CloseQuietly(_client);
                _connections.Unregister(_connection);
            }
        }
    }

    private static ProtocolDetectionResult? DetectProtocol(
        IReadOnlyList<byte[]> pendingJt808Frames,
        IReadOnlyList<byte[]> pendingGt06Frames,
        IReadOnlyList<byte[]> pendingHqFrames,
        int detectionBytes,
        List<byte> prefetched)
    {
        var detection = ProtocolDetector.TryDetect(pendingJt808Frames, pendingGt06Frames, pendingHqFrames)
            ?? ProtocolDetector.TryDetectProtocolOnly(pendingJt808Frames, pendingGt06Frames, pendingHqFrames);

        if (detection == null)
        {
            if (detectionBytes < MaxDetectionBytes || prefetched.Count == 0)
                return null;

            return new ProtocolDetectionResult
            {
                Protocol = ProtocolDetector.GuessProtocol(CollectionsMarshal.AsSpan(prefetched)),
                DeviceId = string.Empty,
                FromRegistry = false
            };
        }

        if (detection.Protocol == DeviceProtocol.Gt06 && pendingGt06Frames.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(detection.DeviceId) && DeviceRegistry.ShouldProxyToRemote(detection.DeviceId))
        {
            return new ProtocolDetectionResult
            {
                Protocol = DeviceProtocol.Gt23,
                DeviceId = detection.DeviceId,
                FromRegistry = true
            };
        }

        return detection;
    }

    private static bool ShouldProxy(ProtocolDetectionResult detection) =>
        detection.Protocol == DeviceProtocol.Gt23
        || DeviceRegistry.ShouldProxyToRemote(detection.DeviceId);

    private async Task RunProxySessionAsync(
        NetworkStream stream,
        IReadOnlyList<byte> prefetched,
        ProtocolDetectionResult detection,
        CancellationToken cancellationToken)
    {
        var deviceId = detection.DeviceId;
        var (host, port) = DeviceRegistry.GetProxyEndpoint(deviceId);
        var deviceLabel = string.IsNullOrWhiteSpace(deviceId) ? "?" : DeviceRegistry.GetDisplayName(deviceId);

        TrafficLogger.LogInfo(
            $"[{deviceLabel}] GT23 proxy → {host}:{port} (сниффинг трафика, телеметрия не пишется)");

        if (!string.IsNullOrWhiteSpace(deviceId))
            _connection.DeviceId = deviceId;

        _connection.Protocol = DeviceProtocol.Gt23;

        var proxy = new ProtocolProxySession(
            host,
            port,
            _connections,
            _connection,
            _client,
            _telemetryStore,
            saveTelemetry: false);

        await proxy.RunWithPrefetchedAsync(stream, prefetched.ToArray(), cancellationToken);
    }

    private async Task RunHqSessionAsync(
        NetworkStream stream,
        IReadOnlyList<byte> prefetched,
        ProtocolDetectionResult detection,
        CancellationToken cancellationToken)
    {
        var deviceLabel = string.IsNullOrWhiteSpace(detection.DeviceId)
            ? "?"
            : DeviceRegistry.GetDisplayName(detection.DeviceId);

        TrafficLogger.LogInfo(
            $"[{deviceLabel}] протокол: HQ{(detection.FromRegistry ? " (из devices.json)" : " (определён автоматически)")}");

        if (!string.IsNullOrWhiteSpace(detection.DeviceId))
            _connection.DeviceId = detection.DeviceId;

        _connection.Protocol = DeviceProtocol.Hq;

        var session = new HqDeviceSession(_connections, _connection, _client, _telemetryStore);
        await session.RunWithPrefetchedAsync(stream, prefetched.ToArray(), cancellationToken);
    }

    private async Task RunLocalSessionAsync(
        NetworkStream stream,
        IReadOnlyList<byte> prefetched,
        CancellationToken cancellationToken)
    {
        var session = new MultiProtocolDeviceSession(_connections, _connection, _client, _telemetryStore);
        await session.RunWithPrefetchedAsync(stream, prefetched.ToArray(), cancellationToken);
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
