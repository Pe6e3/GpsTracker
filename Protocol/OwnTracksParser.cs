using System.Globalization;
using System.Text.Json;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Protocol;

public static class OwnTracksParser
{
    public static bool TryParseTopic(string topic, out string mqttUser, out string deviceId)
    {
        mqttUser = string.Empty;
        deviceId = string.Empty;

        if (string.IsNullOrWhiteSpace(topic))
            return false;

        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return false;

        if (!parts[0].Equals("owntracks", StringComparison.OrdinalIgnoreCase))
            return false;

        mqttUser = parts[1];
        deviceId = parts[2];

        if (deviceId.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.IsNullOrWhiteSpace(deviceId);
    }

    public static OwnTracksLocationMessage? TryParseLocation(string topic, ReadOnlyMemory<byte> payload)
    {
        if (!TryParseTopic(topic, out var mqttUser, out var deviceId))
            return null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!IsType(root, "location"))
                return null;

            if (!TryReadCoordinate(root, "lat", out var latitude))
                return null;

            if (!TryReadCoordinate(root, "lon", out var longitude))
                return null;

            var timestampUtc = ReadTimestampUtc(root);
            var velocityKmh = ReadVelocityKmh(root);
            var trackerId = ReadString(root, "tid");

            return new OwnTracksLocationMessage
            {
                DeviceId = ResolveDeviceId(deviceId, trackerId),
                Topic = topic,
                MqttUser = mqttUser,
                Latitude = latitude,
                Longitude = longitude,
                Accuracy = ReadOptionalDouble(root, "acc"),
                Battery = ReadOptionalInt(root, "batt"),
                TimestampUtc = timestampUtc,
                VelocityKmh = velocityKmh,
                Altitude = ReadOptionalInt(root, "alt"),
                Course = ReadOptionalInt(root, "cog"),
                TrackerId = trackerId
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static OwnTracksStatusMessage? TryParseStatus(string topic, ReadOnlyMemory<byte> payload)
    {
        if (!TryParseTopic(topic, out var mqttUser, out var deviceId))
            return null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!IsType(root, "status"))
                return null;

            return new OwnTracksStatusMessage
            {
                DeviceId = ResolveDeviceId(deviceId, ReadString(root, "tid")),
                Topic = topic,
                MqttUser = mqttUser,
                Battery = ReadOptionalInt(root, "batt"),
                Payload = root.GetRawText()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string BuildReportLocationCommand() =>
        """{"_type":"cmd","action":"reportLocation"}""";

    private static string ResolveDeviceId(string topicDeviceId, string? trackerId)
    {
        // Device ID берём из MQTT topic (3-й сегмент), tid — только метка трекера в OwnTracks.
        if (!string.IsNullOrWhiteSpace(topicDeviceId))
            return topicDeviceId.Trim();

        if (!string.IsNullOrWhiteSpace(trackerId))
            return trackerId.Trim();

        return string.Empty;
    }

    private static bool IsType(JsonElement root, string expectedType)
    {
        if (!root.TryGetProperty("_type", out var typeElement))
            return false;

        return typeElement.GetString()?.Equals(expectedType, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryReadCoordinate(JsonElement root, string property, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(property, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.Number)
        {
            value = element.GetDouble();
            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        return false;
    }

    private static DateTime ReadTimestampUtc(JsonElement root)
    {
        if (root.TryGetProperty("tst", out var tstElement) && tstElement.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds((long)tstElement.GetDouble()).UtcDateTime;

        if (root.TryGetProperty("t", out var tElement) && tElement.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeMilliseconds((long)tElement.GetDouble()).UtcDateTime;

        return DateTime.UtcNow;
    }

    private static double? ReadVelocityKmh(JsonElement root)
    {
        if (!root.TryGetProperty("vel", out var velElement) || velElement.ValueKind != JsonValueKind.Number)
            return null;

        var metersPerSecond = velElement.GetDouble();
        return metersPerSecond * 3.6d;
    }

    private static double? ReadOptionalDouble(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number)
            return null;

        return element.GetDouble();
    }

    private static int? ReadOptionalInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Number)
            return null;

        return (int)Math.Round(element.GetDouble());
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }
}
