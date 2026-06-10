using System.Text;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;
using GpsTcpProxy.Services;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace GpsTcpProxy;

public sealed class MqttService : IAsyncDisposable
{
    private readonly MqttSettings _settings;
    private readonly OwnTracksDeviceHandler _deviceHandler;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IMqttClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public MqttService(MqttSettings settings, OwnTracksDeviceHandler deviceHandler)
    {
        _settings = settings;
        _deviceHandler = deviceHandler;
    }

    public bool IsConnected => _client?.IsConnected == true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            MqttLogger.LogInfo("disabled in config");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts == null)
            return;

        _cts.Cancel();

        if (_runTask != null)
        {
            try
            {
                await _runTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }

        if (_client?.IsConnected == true)
            await _client.DisconnectAsync();

        _client?.Dispose();
        _client = null;
    }

    public async Task SendCommandAsync(string deviceId, string payload, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            throw new InvalidOperationException("MQTT disabled");

        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("MQTT not connected");

        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            normalizedId = deviceId;

        if (DeviceRegistry.GetProtocol(normalizedId) != DeviceProtocol.OwnTracks)
            throw new InvalidOperationException($"Device {normalizedId} is not OwnTracks");

        var commandTopic = ResolveCommandTopic(normalizedId);
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(commandTopic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message, cancellationToken);
        MqttLogger.LogInfo($"command → {commandTopic}: {payload}");
    }

    public Task RequestLocationAsync(string deviceId, CancellationToken cancellationToken = default) =>
        SendCommandAsync(deviceId, OwnTracksParser.BuildReportLocationCommand(), cancellationToken);

    public async Task<OwnTracksLocationMessage?> RequestLocationAndWaitAsync(
        string deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var sinceUtc = DateTime.UtcNow;
        var waitTask = _deviceHandler.WaitForLocationAsync(deviceId, sinceUtc, timeout, cancellationToken);
        await RequestLocationAsync(deviceId, cancellationToken);
        return await waitTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _connectLock.Dispose();
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            var topic = e.ApplicationMessage.Topic ?? string.Empty;
            var payload = e.ApplicationMessage.PayloadSegment.AsMemory();

            if (!topic.EndsWith("/cmd", StringComparison.OrdinalIgnoreCase))
            {
                MqttLogger.LogInfo($"← {topic} ({payload.Length} bytes)");
                if (_settings.LogPayload && payload.Length > 0)
                    MqttLogger.LogPayload(topic, payload);
            }

            _deviceHandler.HandleMessage(topic, payload);

            await Task.CompletedTask;
        };

        _client.DisconnectedAsync += async e =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            MqttLogger.LogError($"disconnected: {e.Reason}");
            await Task.CompletedTask;
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(cancellationToken);
                await WaitForDisconnectAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                MqttLogger.LogError($"loop error: {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            var delay = Math.Max(1, _settings.ReconnectDelaySeconds);
            MqttLogger.LogInfo($"reconnect in {delay}s...");
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client == null)
            return;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_client.IsConnected)
                return;

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId(_settings.ClientId)
                .WithCleanSession();

            if (_settings.UseTls)
            {
                optionsBuilder.WithTcpServer(_settings.Host, _settings.TlsPort);
                optionsBuilder.WithTlsOptions(builder =>
                {
                    builder.UseTls();
                    builder.WithAllowUntrustedCertificates();
                });
            }
            else
                optionsBuilder.WithTcpServer(_settings.Host, _settings.Port);

            if (!string.IsNullOrWhiteSpace(_settings.Username))
                optionsBuilder.WithCredentials(_settings.Username, _settings.Password);

            var options = optionsBuilder.Build();
            var result = await _client.ConnectAsync(options, cancellationToken);

            if (result.ResultCode != MqttClientConnectResultCode.Success)
                throw new InvalidOperationException($"connect failed: {result.ResultCode}");

            var subscribe = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(_settings.Topic))
                .Build();

            await _client.SubscribeAsync(subscribe, cancellationToken);

            var endpoint = _settings.UseTls
                ? $"{_settings.Host}:{_settings.TlsPort} (TLS)"
                : $"{_settings.Host}:{_settings.Port}";

            MqttLogger.LogInfo($"connected to {endpoint}, subscribed {_settings.Topic}");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task WaitForDisconnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _client?.IsConnected == true)
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }

    private string ResolveCommandTopic(string deviceId)
    {
        if (_deviceHandler.TryGetTopicInfo(deviceId, out var topicInfo))
            return topicInfo.CommandTopic;

        var user = string.IsNullOrWhiteSpace(_settings.OwnTracksUser)
            ? "user"
            : _settings.OwnTracksUser;

        return $"owntracks/{user}/{deviceId}/cmd";
    }
}
