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
    private bool _registrationCompleted;

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

            if (!message.ChecksumValid)
                TrafficLogger.LogInfo($"[{DeviceRegistry.GetDisplayName(_connection.DeviceLabel)}] checksum=INVALID msg=0x{message.MessageId:X4}");

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

            foreach (var response in BuildResponses(message))
                await SendFrameAsync(stream, response, cancellationToken);
        }
    }

    private IEnumerable<byte[]> BuildResponses(Jt808Message message)
    {
        var terminalId = message.TerminalId;
        if (string.IsNullOrWhiteSpace(terminalId))
            yield break;

        Jt808Parser.TryGetTerminalIdBytes(message.RawFrame, out var terminalIdBytes);

        switch (message.MessageId)
        {
            case Jt808Parser.MsgRegistration:
                _registrationCompleted = true;
                TrafficLogger.LogInfo($"[{DeviceRegistry.GetDisplayName(terminalId)}] регистрация 0x0100 → 0x8100");
                yield return BuildRegistrationResponse(message, message.Serial, BuildDefaultAuthCode(terminalId));
                yield break;

            case Jt808Parser.MsgAuthentication:
                if (!IsAuthAccepted(message))
                {
                    TrafficLogger.LogInfo($"[{DeviceRegistry.GetDisplayName(terminalId)}] аутентификация отклонена");
                    yield return BuildGeneralResponse(message, result: 1);
                    yield break;
                }

                if (!_registrationCompleted)
                {
                    _registrationCompleted = true;
                    var authCode = message.Body.Length > 0 ? message.Body : BuildDefaultAuthCode(terminalId);
                    TrafficLogger.LogInfo($"[{DeviceRegistry.GetDisplayName(terminalId)}] 0x0100 не было → 0x8100 перед 0x8001");
                    yield return BuildRegistrationResponse(message, message.Serial, authCode);
                }

                TrafficLogger.LogInfo($"[{DeviceRegistry.GetDisplayName(terminalId)}] аутентификация принята → 0x8001");
                yield return BuildGeneralResponse(message, result: 0);
                yield break;

            case Jt808Parser.MsgTimeSyncRequest:
                yield return Jt808Encoder.BuildTimeSyncResponse(
                    terminalId,
                    NextSerial(),
                    DateTime.UtcNow.AddHours(AppTime.DeviceUtcOffset));
                yield break;

            case Jt808Parser.MsgHeartbeat:
            case Jt808Parser.MsgLocationReport:
                yield return BuildGeneralResponse(message, result: 0);
                yield break;

            default:
                yield return BuildGeneralResponse(message, result: 0);
                yield break;
        }
    }

    private static byte[] BuildRegistrationResponse(Jt808Message message, ushort originalSerial, byte[] authCode)
    {
        Jt808Parser.TryGetTerminalIdBytes(message.RawFrame, out var terminalIdBytes);
        return Jt808Encoder.BuildRegistrationResponse(
            message.TerminalId,
            terminalIdBytes,
            PlatformSerial.Next(),
            originalSerial,
            result: 0,
            authCode);
    }

    private static byte[] BuildDefaultAuthCode(string terminalId)
    {
        var authCode = new byte[7];
        Jt808Bcd.EncodeTerminalId(terminalId).CopyTo(authCode, 0);
        authCode[6] = 0x01;
        return authCode;
    }

    private static byte[] BuildGeneralResponse(Jt808Message message, byte result)
    {
        Jt808Parser.TryGetTerminalIdBytes(message.RawFrame, out var terminalIdBytes);
        return Jt808Encoder.BuildGeneralResponse(
            message.TerminalId,
            terminalIdBytes,
            PlatformSerial.Next(),
            message.Serial,
            message.MessageId,
            result);
    }

    private static ushort NextSerial() => PlatformSerial.Next();

    private async Task SendFrameAsync(NetworkStream stream, byte[] frame, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        RawDataLogger.LogPacket(_connection, PacketLogDirection.ToDevice, frame);
    }

    private static bool IsAuthAccepted(Jt808Message message)
    {
        if (string.IsNullOrWhiteSpace(message.TerminalId) || message.Body.Length == 0)
            return false;

        var headerNorm = DeviceRegistry.NormalizeId(message.TerminalId);

        if (message.Body.Length >= 6)
        {
            var bodyNorm = DeviceRegistry.NormalizeId(Jt808Bcd.DecodeDigits(message.Body.AsSpan(0, 6)));
            if (bodyNorm == headerNorm)
                return true;

            var expected = Jt808Bcd.EncodeTerminalId(message.TerminalId);
            if (message.Body.AsSpan(0, 6).SequenceEqual(expected))
                return true;
        }

        var token = Jt808Bcd.DecodeDigits(message.Body);
        if (token.StartsWith(message.TerminalId, StringComparison.Ordinal))
            return true;

        if (token.StartsWith(headerNorm, StringComparison.Ordinal) ||
            token.StartsWith($"1{headerNorm}", StringComparison.Ordinal))
            return true;

        return DeviceRegistry.NormalizeId(token) == headerNorm;
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
