using System.Net;
using System.Net.Sockets;
using GpsTcpProxy;

var baseDir = AppContext.BaseDirectory;
var settings = ProxySettings.Load(Path.Combine(baseDir, "appsettings.json"));
AppTime.Configure(settings);
DeviceRegistry.Load(Path.Combine(baseDir, "devices.json"));
var connections = new ConnectionManager();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    TrafficLogger.LogInfo("Получен сигнал остановки (Ctrl+C)");
};

var listener = new TcpListener(IPAddress.Any, settings.ListenPort);
listener.Start();

TrafficLogger.LogInfo("GPS TCP Proxy запущен");
TrafficLogger.LogInfo($"Слушаю порт: {settings.ListenPort}");
TrafficLogger.LogInfo($"Перенаправление на: {settings.RemoteHost}:{settings.RemotePort}");
TrafficLogger.LogInfo("Raw-лог: logs/raw_data/");
TrafficLogger.LogInfo($"Часовой пояс логов: UTC{(settings.UtcOffset >= 0 ? "+" : "")}{settings.UtcOffset}");
TrafficLogger.LogInfo($"Часовой пояс устройства: UTC{(settings.DeviceUtcOffset >= 0 ? "+" : "")}{settings.DeviceUtcOffset}");
TrafficLogger.LogInfo("Ожидание подключений GPS-трекера...");

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(cts.Token);
        var clientIp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var connection = connections.Register(clientIp);

        _ = Task.Run(async () =>
        {
            try
            {
                var session = new ProxySession(settings, connections, connection, client);
                await session.RunAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                TrafficLogger.LogInfo($"Ошибка сессии: {ex.Message}");
            }
        }, CancellationToken.None);
    }
}
catch (OperationCanceledException)
{
    TrafficLogger.LogInfo("Прокси остановлен");
}
finally
{
    listener.Stop();
    TrafficLogger.LogInfo("TCP-сервер остановлен");
}
