using System.Net.Sockets;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class ProxySession
{
    private const int BufferSize = 65536;

    private readonly ProxySettings _settings;
    private readonly ConnectionManager _connections;
    private readonly ProxyConnection _connection;
    private readonly TcpClient _client;
    private readonly TelemetryStore _telemetryStore;
    private readonly Jt808FrameBuffer _clientBuffer = new();
    private readonly Jt808FrameBuffer _serverBuffer = new();

    public ProxySession(
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
                _clientBuffer,
                "IN",
                cancellationToken);

            var serverToClient = PumpAsync(
                remoteStream,
                clientStream,
                _serverBuffer,
                "OUT",
                cancellationToken);

            await Task.WhenAny(clientToServer, serverToClient);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
        finally
        {
            CloseQuietly(_client, "клиент");
            if (remote != null)
                CloseQuietly(remote, "удалённый сервер");

            _connections.Unregister(_connection);
        }
    }

    private async Task<string> PumpAsync(
        NetworkStream source,
        NetworkStream destination,
        Jt808FrameBuffer frameBuffer,
        string direction,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                    return direction == "IN"
                        ? "клиент завершил передачу (EOF)"
                        : "сервер производителя завершил передачу (EOF)";

                var chunk = buffer.AsMemory(0, bytesRead);
                ProcessChunk(frameBuffer, direction, chunk.Span);
                await destination.WriteAsync(chunk, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            return "отмена операции";
        }
        catch (IOException ex)
        {
            return $"IO ошибка ({direction}): {ex.Message}";
        }
        catch (SocketException ex)
        {
            return $"сокет закрыт ({direction}): {ex.Message}";
        }
        catch (ObjectDisposedException)
        {
            return $"поток закрыт ({direction})";
        }
    }

    private void ProcessChunk(Jt808FrameBuffer frameBuffer, string direction, ReadOnlySpan<byte> chunk)
    {
        frameBuffer.Append(chunk);

        foreach (var frame in frameBuffer.ExtractFrames())
        {
            RawDataLogger.LogPacket(_connection, direction, frame);

            if (direction != "IN")
                continue;

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
        }
    }

    private static void CloseQuietly(TcpClient client, string name)
    {
        try
        {
            client.Close();
        }
        catch (Exception ex)
        {
            TrafficLogger.LogInfo($"Ошибка при закрытии {name}: {ex.Message}");
        }
    }
}
