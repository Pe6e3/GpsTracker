using System.Net.Sockets;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class Gt06DeviceSession
{
    private readonly ConnectionManager _connections;
    private readonly ProxyConnection _connection;
    private readonly TcpClient _client;
    private readonly TelemetryStore _telemetryStore;
    private readonly Gt06FrameBuffer _frameBuffer = new();

    public Gt06DeviceSession(
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
        try
        {
            using var stream = _client.GetStream();
            var buffer = new byte[65536];

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
            TrafficLogger.LogInfo($"IO ошибка GT06-сессии #{_connection.ConnectionId}: {ex.Message}");
        }
        catch (SocketException ex)
        {
            TrafficLogger.LogInfo($"Сокет закрыт GT06-сессии #{_connection.ConnectionId}: {ex.Message}");
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            CloseQuietly(_client);
            _connections.Unregister(_connection);
        }
    }

    public async Task ProcessInitialChunkAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        await ProcessChunkAsync(stream, chunk, cancellationToken);
    }

    public async Task ProcessFrameAsync(
        NetworkStream stream,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        await HandleFrameAsync(stream, frame, cancellationToken);
    }

    private async Task ProcessChunkAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        _frameBuffer.Append(chunk.Span);

        foreach (var frame in _frameBuffer.ExtractFrames())
            await HandleFrameAsync(stream, frame, cancellationToken);
    }

    private async Task HandleFrameAsync(
        NetworkStream stream,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        RawDataLogger.LogPacket(_connection, PacketLogDirection.FromDevice, frame);

        if (!Gt06Parser.TryParseFrame(frame, out var message) || message == null)
        {
            TrafficLogger.LogUnparsedDeviceData(_connection, frame.Length, DeviceProtocol.Gt06);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Imei))
            _connection.DeviceId = message.Imei;

        var deviceName = DeviceRegistry.GetDisplayName(_connection.DeviceLabel);

        if (!message.ChecksumValid)
            TrafficLogger.LogInfo($"[{deviceName}] GT06 checksum=INVALID proto=0x{message.ProtocolNumber:X2}");

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

        foreach (var response in BuildResponses(message, deviceName))
            await SendFrameAsync(stream, response, cancellationToken);
    }

    private static IEnumerable<byte[]> BuildResponses(Gt06Message message, string deviceName)
    {
        switch (message.ProtocolNumber)
        {
            case Gt06Parser.ProtocolLogin:
                TrafficLogger.LogInfo($"[{deviceName}] login 0x01 → ACK 0x01 serial=0x{message.Serial:X4}");
                yield return Gt06Encoder.BuildAck(message.ProtocolNumber, message.Serial);
                yield break;

            case Gt06Parser.ProtocolHeartbeat:
                TrafficLogger.LogInfo($"[{deviceName}] heartbeat 0x13 → ACK 0x13 serial=0x{message.Serial:X4}");
                yield return Gt06Encoder.BuildAck(message.ProtocolNumber, message.Serial);
                yield break;

            case Gt06Parser.ProtocolLocation:
                yield return Gt06Encoder.BuildAck(message.ProtocolNumber, message.Serial);
                yield break;

            case Gt06Parser.ProtocolStatus:
                TrafficLogger.LogInfo($"[{deviceName}] status 0x12 → ACK 0x12 serial=0x{message.Serial:X4}");
                yield return Gt06Encoder.BuildAck(message.ProtocolNumber, message.Serial);
                yield break;

            default:
                if (ShouldAck(message.ProtocolNumber))
                    yield return Gt06Encoder.BuildAck(message.ProtocolNumber, message.Serial);
                yield break;
        }
    }

    private static bool ShouldAck(byte protocolNumber) =>
        protocolNumber is Gt06Parser.ProtocolLogin
            or Gt06Parser.ProtocolHeartbeat
            or Gt06Parser.ProtocolLocation
            or Gt06Parser.ProtocolStatus;

    private async Task SendFrameAsync(NetworkStream stream, byte[] frame, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        RawDataLogger.LogPacket(_connection, PacketLogDirection.ToDevice, frame);
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
