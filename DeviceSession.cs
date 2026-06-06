using System.Net.Sockets;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class DeviceSession
{
    private const int BufferSize = 65536;

    private readonly ConnectionManager _connections;
    private readonly ProxyConnection _connection;
    private readonly TcpClient _client;
    private readonly TelemetryStore _telemetryStore;
    private readonly Jt808FrameBuffer _frameBuffer = new();
    private ushort _serverSerial;

    public DeviceSession(
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
            var buffer = new byte[BufferSize];

            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                    break;

                var chunk = buffer.AsMemory(0, bytesRead);
                await ProcessChunkAsync(stream, chunk, cancellationToken);
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
            CloseQuietly(_client);
            _connections.Unregister(_connection);
        }
    }

    private async Task ProcessChunkAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        _frameBuffer.Append(chunk.Span);

        foreach (var frame in _frameBuffer.ExtractFrames())
        {
            RawDataLogger.LogPacket(_connection, PacketLogDirection.FromDevice, frame);

            if (!Jt808Parser.TryParseFrame(frame, out var message) || message == null)
            {
                TrafficLogger.LogUnparsedDeviceData(_connection, frame.Length);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.TerminalId))
                _connection.DeviceId = message.TerminalId;

            if (!Jt808PacketTypes.IsFromTerminal(message.MessageId))
                continue;

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

            var response = BuildResponse(message);
            if (response == null)
                continue;

            await stream.WriteAsync(response, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            RawDataLogger.LogPacket(_connection, PacketLogDirection.ToDevice, response);
        }
    }

    private byte[]? BuildResponse(Jt808Message message)
    {
        if (!message.ChecksumValid)
            return null;

        var terminalId = message.TerminalId;
        if (string.IsNullOrWhiteSpace(terminalId))
            return null;

        return message.MessageId switch
        {
            Jt808Parser.MsgRegistration => Jt808Encoder.BuildRegistrationResponse(
                terminalId,
                NextSerial(),
                message.Serial,
                result: 0,
                authCode: terminalId),

            Jt808Parser.MsgAuthentication or
            Jt808Parser.MsgHeartbeat or
            Jt808Parser.MsgLocationReport => Jt808Encoder.BuildGeneralResponse(
                terminalId,
                NextSerial(),
                message.Serial,
                message.MessageId,
                result: 0),

            _ => Jt808Encoder.BuildGeneralResponse(
                terminalId,
                NextSerial(),
                message.Serial,
                message.MessageId,
                result: 0)
        };
    }

    private ushort NextSerial() => ++_serverSerial;

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
