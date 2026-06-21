using System.Net.Sockets;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class HqDeviceSession
{
    private readonly ConnectionManager _connections;
    private readonly ProxyConnection _connection;
    private readonly TcpClient _client;
    private readonly TelemetryStore _telemetryStore;
    private readonly HqFrameBuffer _frameBuffer = new();

    public HqDeviceSession(
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
        try
        {
            if (!prefetched.IsEmpty)
                await ProcessChunkAsync(stream, prefetched, cancellationToken);

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
            TrafficLogger.LogInfo($"IO ошибка HQ-сессии #{_connection.ConnectionId}: {ex.Message}");
        }
        catch (SocketException ex)
        {
            TrafficLogger.LogInfo($"Сокет закрыт HQ-сессии #{_connection.ConnectionId}: {ex.Message}");
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

        if (!HqParser.TryParseFrame(frame, out var message) || message == null)
        {
            TrafficLogger.LogUnparsedDeviceData(_connection, frame.Length, DeviceProtocol.Hq);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.DeviceId))
            _connection.DeviceId = message.DeviceId;

        _connection.Protocol = DeviceProtocol.Hq;

        if (message.Location != null)
        {
            try
            {
                _telemetryStore.SaveHqLocation(message);
            }
            catch (Exception ex)
            {
                TrafficLogger.LogInfo($"Ошибка записи телеметрии HQ: {ex.Message}");
            }
        }

        TrafficLogger.LogHqDevicePacket(_connection, message);

        if (ShouldSendAck(message))
            await SendFrameAsync(stream, HqEncoder.BuildR12Ack(message.DeviceId, AppTime.NowLocal()), cancellationToken);
    }

    private static bool ShouldSendAck(HqMessage message) =>
        message.IsBinary
        || message.PacketType.Equals(HqParser.PacketTypeTelemetry, StringComparison.OrdinalIgnoreCase);

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
