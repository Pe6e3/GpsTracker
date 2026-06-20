namespace GpsTcpProxy;

public static class MqttLogger
{
    public static void LogInfo(string message) =>
        WriteLine($"[MQTT] {message}");

    public static void LogError(string message) =>
        WriteLine($"[MQTT ERROR] {message}");

    public static void LogLocation(
        string deviceName,
        double? latitude,
        double? longitude,
        double? accuracyMeters,
        int? batteryPercent)
    {
        var batteryText = batteryPercent.HasValue ? $"🔋{batteryPercent.Value}%" : "🔋—";

        if (latitude.HasValue && longitude.HasValue)
        {
            var accuracyText = accuracyMeters.HasValue
                ? $"точность {accuracyMeters.Value:F0}m"
                : "точность —";

            WriteLine($"[MQTT📡] {deviceName}: {latitude.Value:F5},{longitude.Value:F5} {accuracyText}, {batteryText}");
            return;
        }

        var noCoordsAccuracy = accuracyMeters.HasValue
            ? $"точность {accuracyMeters.Value:F0}m"
            : "точность —";

        WriteLine($"[MQTT📡] {deviceName}: нет координат ({noCoordsAccuracy}), {batteryText}");
    }

    private static void WriteLine(string message) =>
        TrafficLogger.LogInfo(message);
}
