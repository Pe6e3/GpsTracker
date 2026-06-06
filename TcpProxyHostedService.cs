using System.Net;
using System.Net.Sockets;

namespace GpsTcpProxy;

public sealed class TcpProxyHostedService : IHostedService
{
    private readonly ProxySettings _settings;
    private readonly ConnectionManager _connections;
    private readonly TelemetryStore _telemetryStore;
    private readonly IHostApplicationLifetime _lifetime;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TcpListener? _listener;

    public TcpProxyHostedService(
        ProxySettings settings,
        ConnectionManager connections,
        TelemetryStore telemetryStore,
        IHostApplicationLifetime lifetime)
    {
        _settings = settings;
        _connections = connections;
        _telemetryStore = telemetryStore;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(OnApplicationStarted);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts == null)
            return;

        _cts.Cancel();

        if (_runTask != null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listener?.Stop();
    }

    private void OnApplicationStarted()
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        _runTask = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Any, _settings.ListenPort);

        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            TrafficLogger.LogInfo($"Не удалось запустить TCP-прокси на порту {_settings.ListenPort}: {ex.Message}");
            return;
        }

        TrafficLogger.LogInfo("GPS TCP Proxy запущен");
        TrafficLogger.LogInfo($"Слушаю порт: {_settings.ListenPort}");
        TrafficLogger.LogInfo($"Перенаправление на: {_settings.RemoteHost}:{_settings.RemotePort}");
        TrafficLogger.LogInfo("Raw-лог: logs/raw_data/");
        TrafficLogger.LogInfo($"Часовой пояс логов: {AppTime.FormatUtcOffset(_settings.UtcOffset)}");
        TrafficLogger.LogInfo($"Часовой пояс сервера: {AppTime.FormatUtcOffset(_settings.ServerUtcOffset)}");
        TrafficLogger.LogInfo($"Часовой пояс устройства: {AppTime.FormatUtcOffset(_settings.DeviceUtcOffset)}");
        TrafficLogger.LogInfo($"База телеметрии: {_settings.DatabasePath}");
        TrafficLogger.LogInfo("Ожидание подключений GPS-трекера...");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                var clientIp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                var connection = _connections.Register(clientIp);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var session = new ProxySession(_settings, _connections, connection, client, _telemetryStore);
                        await session.RunAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        TrafficLogger.LogInfo($"Ошибка сессии: {ex.Message}");
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            TrafficLogger.LogInfo("Прокси остановлен");
        }
        finally
        {
            _listener.Stop();
            TrafficLogger.LogInfo("TCP-сервер остановлен");
        }
    }
}
