using System.Net.Sockets;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class MultiProtocolProxySession
{
    private const int BufferSize = 65536;
    private const int MaxDetectionBytes = 16384;

    private readonly ProxySettings _settings;
    private readonly ConnectionManager _connections;
    private readonly ProxyConnection _connection;
    private readonly TcpClient _client;
    private readonly TelemetryStore _telemetryStore;
    private readonly Jt808FrameBuffer _clientJt808Buffer = new();
    private readonly Gt06FrameBuffer _clientGt06Buffer = new();
    private readonly Jt808FrameBuffer _serverBuffer = new();
    private DeviceProtocol? _protocol;
    private int _detectionBytes;

    public MultiProtocolProxySession(
        ProxySettings settings,
        ConnectionManager connections,
        ProxyConnection connection,
        TcpClient client,
        TelemetryStore telemetryStore)
    {
        _settings = settings;
        _connections = connections;
        _connection = connection;
        _client = client;
        _telemetryStore = telemetryStore;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        TcpClient? remote = null;

        try
        {
            remote = new TcpClient();
            await remote.ConnectAsync(_settings.RemoteHost, _settings.RemotePort, cancellationToken);

            using var clientStream = _client.GetStream();
            using var remoteStream = remote.GetStream();

            var clientToServer = PumpAsync(
                clientStream,
                remoteStream,
                PacketLogDirection.FromDevice,
                cancellationToken);

            var serverToClient = PumpAsync(
                remoteStream,
                clientStream,
                PacketLogDirection.ToDevice,
                cancellationToken);

            await Task.WhenAny(clientToServer, serverToClient);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TrafficLogger.LogInfo($"Ошибка прокси-сессии #{_connection.ConnectionId}: {ex.Message}");
        }
        finally
        {
            CloseQuietly(_client);
            if (remote != null)
                CloseQuietly(remote);

            _connections.Unregister(_connection);
        }
    }

    private async Task PumpAsync(
        NetworkStream source,
        NetworkStream destination,
        string logDirection,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var parseDevicePackets = logDirection == PacketLogDirection.FromDevice;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                    break;

                var chunk = buffer.AsMemory(0, bytesRead);
                if (parseDevicePackets)
                    ProcessDeviceChunk(chunk.Span);
                else
                    ProcessRemoteChunk(chunk.Span, logDirection);

                await destination.WriteAsync(chunk, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ProcessDeviceChunk(ReadOnlySpan<byte> chunk)
    {
        _clientJt808Buffer.Append(chunk);
        _clientGt06Buffer.Append(chunk);

        var jt808Frames = _clientJt808Buffer.ExtractFrames();
        var gt06Frames = _clientGt06Buffer.ExtractFrames();

        if (_protocol == null)
        {
            _detectionBytes += chunk.Length;
            TryDetectProtocol(jt808Frames, gt06Frames, chunk);
        }

        if (_protocol == null || _protocol == DeviceProtocol.Jt808)
        {
            foreach (var frame in jt808Frames)
                ProcessJt808DeviceFrame(frame);
        }

        if (_protocol == null || _protocol == DeviceProtocol.Gt06)
        {
            foreach (var frame in gt06Frames)
                ProcessGt06DeviceFrame(frame);
        }
    }

    private void TryDetectProtocol(IReadOnlyList<byte[]> jt808Frames, IReadOnlyList<byte[]> gt06Frames, ReadOnlySpan<byte> chunk)
    {
        var detection = ProtocolDetector.TryDetect(jt808Frames, gt06Frames)
            ?? ProtocolDetector.TryDetectProtocolOnly(jt808Frames, gt06Frames);

        if (detection == null)
        {
            if (_detectionBytes < MaxDetectionBytes)
                return;

            detection = new ProtocolDetectionResult
            {
                Protocol = ProtocolDetector.GuessProtocol(chunk),
                DeviceId = string.Empty,
                FromRegistry = false
            };
        }

        if (detection.Protocol == DeviceProtocol.Gt06 && gt06Frames.Count == 0)
            return;

        _protocol = detection.Protocol;
        _connection.Protocol = detection.Protocol;

        if (!string.IsNullOrWhiteSpace(detection.DeviceId))
            _connection.DeviceId = detection.DeviceId;

        var deviceLabel = string.IsNullOrWhiteSpace(detection.DeviceId) ? "?" : DeviceRegistry.GetDisplayName(detection.DeviceId);
        TrafficLogger.LogInfo(
            $"[{deviceLabel}] протокол: {DeviceProtocolParser.ToConfigValue(detection.Protocol)}{(detection.FromRegistry ? " (из devices.json)" : " (определён автоматически)")}");
    }

    private void ProcessJt808DeviceFrame(byte[] frame)
    {
        if (_protocol == DeviceProtocol.Gt06)
            return;

        RawDataLogger.LogPacket(_connection, PacketLogDirection.FromDevice, frame);

        if (!Jt808Parser.TryParseFrame(frame, out var message) || message == null)
        {
            TrafficLogger.LogUnparsedDeviceData(_connection, frame.Length, DeviceProtocol.Jt808);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.TerminalId))
            _connection.DeviceId = message.TerminalId;

        if (!Jt808PacketTypes.IsFromTerminal(message.MessageId))
            return;

        if (message.MessageId == Jt808Parser.MsgLocationReport)
        {
            try
            {
                _telemetryStore.SaveLocation(message);
            }
            catch (Exception ex)
            {
                TrafficLogger.LogInfo($"Ошибка записи телеметрии: {ex.Message}");
            }
        }

        TrafficLogger.LogDevicePacket(_connection, message);
    }

    private void ProcessGt06DeviceFrame(byte[] frame)
    {
        if (_protocol == DeviceProtocol.Jt808)
            return;

        RawDataLogger.LogPacket(_connection, PacketLogDirection.FromDevice, frame);

        if (!Gt06Parser.TryParseFrame(frame, out var message) || message == null)
        {
            TrafficLogger.LogUnparsedDeviceData(_connection, frame.Length, DeviceProtocol.Gt06);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Imei))
            _connection.DeviceId = message.Imei;

        if (message.ProtocolNumber == Gt06Parser.ProtocolLocation)
        {
            try
            {
                _telemetryStore.SaveGt06Location(_connection.DeviceLabel, message);
            }
            catch (Exception ex)
            {
                TrafficLogger.LogInfo($"Ошибка записи телеметрии GT06: {ex.Message}");
            }
        }

        TrafficLogger.LogGt06DevicePacket(_connection, message);
    }

    private void ProcessRemoteChunk(ReadOnlySpan<byte> chunk, string logDirection)
    {
        _serverBuffer.Append(chunk);

        foreach (var frame in _serverBuffer.ExtractFrames())
            RawDataLogger.LogPacket(_connection, logDirection, frame);
    }

    private static void CloseQuietly(TcpClient client)
    {
        try
        {
            client.Close();
        }
        catch
        {
        }
    }
}
