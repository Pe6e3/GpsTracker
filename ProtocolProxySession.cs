using System.Net.Sockets;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class ProtocolProxySession
{
    private const int BufferSize = 65536;

    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly bool _saveTelemetry;
    private readonly ConnectionManager _connections;
    private readonly ProxyConnection _connection;
    private readonly TcpClient _client;
    private readonly TelemetryStore _telemetryStore;
    private readonly Jt808FrameBuffer _clientJt808Buffer = new();
    private readonly Gt06FrameBuffer _clientGt06Buffer = new();
    private readonly Jt808FrameBuffer _serverJt808Buffer = new();
    private readonly Gt06FrameBuffer _serverGt06Buffer = new();
    private DeviceProtocol? _protocol;

    public ProtocolProxySession(
        string remoteHost,
        int remotePort,
        ConnectionManager connections,
        ProxyConnection connection,
        TcpClient client,
        TelemetryStore telemetryStore,
        bool saveTelemetry = false)
    {
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _saveTelemetry = saveTelemetry;
        _connections = connections;
        _connection = connection;
        _client = client;
        _telemetryStore = telemetryStore;
    }

    public async Task RunWithPrefetchedAsync(
        NetworkStream clientStream,
        ReadOnlyMemory<byte> prefetched,
        CancellationToken cancellationToken)
    {
        TcpClient? remote = null;

        try
        {
            remote = new TcpClient();
            await remote.ConnectAsync(_remoteHost, _remotePort, cancellationToken);

            using var remoteStream = remote.GetStream();

            if (!prefetched.IsEmpty)
            {
                ProcessDeviceChunk(prefetched.Span);
                await remoteStream.WriteAsync(prefetched, cancellationToken);
                await remoteStream.FlushAsync(cancellationToken);
            }

            var clientToServer = PumpAsync(
                clientStream,
                remoteStream,
                PacketLogDirection.FromDevice,
                parseDevicePackets: true,
                cancellationToken);

            var serverToClient = PumpAsync(
                remoteStream,
                clientStream,
                PacketLogDirection.ToDevice,
                parseDevicePackets: false,
                cancellationToken);

            await Task.WhenAny(clientToServer, serverToClient);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TrafficLogger.LogInfo($"Ошибка прокси-сессии #{_connection.ConnectionId} → {_remoteHost}:{_remotePort}: {ex.Message}");
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
        bool parseDevicePackets,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];

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
            TryDetectProtocol(jt808Frames, gt06Frames);

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

    private void TryDetectProtocol(IReadOnlyList<byte[]> jt808Frames, IReadOnlyList<byte[]> gt06Frames)
    {
        var detection = ProtocolDetector.TryDetect(jt808Frames, gt06Frames)
            ?? ProtocolDetector.TryDetectProtocolOnly(jt808Frames, gt06Frames);

        if (detection == null)
            return;

        if (detection.Protocol == DeviceProtocol.Gt06 && gt06Frames.Count == 0)
            return;

        _protocol = detection.Protocol == DeviceProtocol.Gt23
            ? ProtocolDetector.GuessProtocol(jt808Frames.Count > 0 ? jt808Frames[0] : gt06Frames[0])
            : detection.Protocol;
        _connection.Protocol = _protocol;

        if (!string.IsNullOrWhiteSpace(detection.DeviceId))
            _connection.DeviceId = detection.DeviceId;

        var deviceLabel = string.IsNullOrWhiteSpace(detection.DeviceId)
            ? "?"
            : DeviceRegistry.GetDisplayName(detection.DeviceId);

        TrafficLogger.LogInfo(
            $"[{deviceLabel}] прокси-трафик: {DeviceProtocolParser.ToConfigValue(_protocol.Value)} (удалённый сервер)");
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

        if (_saveTelemetry && message.MessageId == Jt808Parser.MsgLocationReport)
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

        if (_saveTelemetry && message.ProtocolNumber == Gt06Parser.ProtocolLocation)
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
        _serverJt808Buffer.Append(chunk);
        _serverGt06Buffer.Append(chunk);

        foreach (var frame in _serverJt808Buffer.ExtractFrames())
            RawDataLogger.LogPacket(_connection, logDirection, frame);

        foreach (var frame in _serverGt06Buffer.ExtractFrames())
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
