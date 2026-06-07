using System.Collections.Concurrent;
using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy.Services;

public sealed class OwnTracksDeviceHandler
{
    private readonly TelemetryStore _telemetryStore;
    private readonly ConcurrentDictionary<string, OwnTracksTopicInfo> _topics = new(StringComparer.OrdinalIgnoreCase);

    public OwnTracksDeviceHandler(TelemetryStore telemetryStore)
    {
        _telemetryStore = telemetryStore;
    }

    public void HandleMessage(string topic, ReadOnlyMemory<byte> payload)
    {
        if (topic.EndsWith("/cmd", StringComparison.OrdinalIgnoreCase))
            return;

        var location = OwnTracksParser.TryParseLocation(topic, payload);
        if (location != null)
        {
            RegisterTopic(location.DeviceId, location.MqttUser, topic);
            HandleLocation(location);
            return;
        }

        var status = OwnTracksParser.TryParseStatus(topic, payload);
        if (status != null)
        {
            RegisterTopic(status.DeviceId, status.MqttUser, topic);
            HandleStatus(status);
            return;
        }

        if (topic.EndsWith("/status", StringComparison.OrdinalIgnoreCase) &&
            OwnTracksParser.TryParseTopic(topic, out _, out var topicDeviceId))
            RegisterTopic(topicDeviceId, null, topic);
    }

    public bool TryGetTopicInfo(string deviceId, out OwnTracksTopicInfo topicInfo) =>
        _topics.TryGetValue(DeviceRegistry.NormalizeId(deviceId), out topicInfo!) ||
        _topics.TryGetValue(deviceId, out topicInfo!);

    public void RegisterTopic(string deviceId, string? mqttUser, string topic)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(topic))
            return;

        if (!OwnTracksParser.TryParseTopic(topic, out var parsedUser, out var parsedDevice))
            return;

        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            normalizedId = deviceId.Trim();

        var user = string.IsNullOrWhiteSpace(mqttUser) ? parsedUser : mqttUser;
        var topicBase = $"owntracks/{user}/{parsedDevice}";

        _topics[normalizedId] = new OwnTracksTopicInfo
        {
            DeviceId = normalizedId,
            MqttUser = user,
            TopicBase = topicBase
        };
    }

    private void HandleLocation(OwnTracksLocationMessage location)
    {
        var deviceId = DeviceRegistry.NormalizeId(location.DeviceId);
        if (string.IsNullOrEmpty(deviceId))
            deviceId = location.DeviceId;

        if (!ShouldAcceptDevice(deviceId))
        {
            MqttLogger.LogInfo($"location ignored: device {deviceId} is not registered");
            return;
        }

        try
        {
            _telemetryStore.SaveOwnTracksLocation(deviceId, location);
        }
        catch (Exception ex)
        {
            MqttLogger.LogError($"telemetry save failed for {deviceId}: {ex.Message}");
            return;
        }

        var deviceName = DeviceRegistry.GetDisplayName(deviceId);
        if (location.Accuracy.HasValue && location.Accuracy.Value > TelemetryStore.OwnTracksNoCoordinatesAccuracyM)
        {
            MqttLogger.LogInfo(
                $"location {deviceName}: no coords (acc={location.Accuracy:F0}m) batt={location.Battery}%");
            return;
        }

        MqttLogger.LogInfo(
            $"location {deviceName}: {location.Latitude:F5},{location.Longitude:F5} acc={location.Accuracy:F0}m batt={location.Battery}%");
    }

    private void HandleStatus(OwnTracksStatusMessage status)
    {
        var deviceId = DeviceRegistry.NormalizeId(status.DeviceId);
        if (string.IsNullOrEmpty(deviceId))
            deviceId = status.DeviceId;

        if (!ShouldAcceptDevice(deviceId))
        {
            MqttLogger.LogInfo($"status ignored: device {deviceId} is not registered");
            return;
        }

        var deviceName = DeviceRegistry.GetDisplayName(deviceId);
        MqttLogger.LogInfo($"status {deviceName}: batt={status.Battery}% topic={status.Topic}");
    }

    private static bool ShouldAcceptDevice(string deviceId)
    {
        if (!DeviceRegistry.Exists(deviceId))
            return false;

        return DeviceRegistry.GetProtocol(deviceId) == DeviceProtocol.OwnTracks;
    }
}
