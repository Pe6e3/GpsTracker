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
                PacketLogDirection.FromDevice,
                cancellationToken);

            var serverToClient = PumpAsync(
                remoteStream,
                clientStream,
                _serverBuffer,
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
        Jt808FrameBuffer frameBuffer,
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
                    ProcessDeviceChunk(frameBuffer, chunk.Span);
                else
                    ProcessRemoteChunk(frameBuffer, chunk.Span, logDirection);

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

    private void ProcessDeviceChunk(Jt808FrameBuffer frameBuffer, ReadOnlySpan<byte> chunk)
    {
        frameBuffer.Append(chunk);

        foreach (var frame in frameBuffer.ExtractFrames())
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
        }
    }

    private void ProcessRemoteChunk(Jt808FrameBuffer frameBuffer, ReadOnlySpan<byte> chunk, string logDirection)
    {
        frameBuffer.Append(chunk);

        foreach (var frame in frameBuffer.ExtractFrames())
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
