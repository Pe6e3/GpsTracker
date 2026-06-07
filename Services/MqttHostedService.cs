namespace GpsTcpProxy.Services;

public sealed class MqttHostedService : IHostedService
{
    private readonly MqttService _mqttService;

    public MqttHostedService(MqttService mqttService)
    {
        _mqttService = mqttService;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _mqttService.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _mqttService.StopAsync();
}
