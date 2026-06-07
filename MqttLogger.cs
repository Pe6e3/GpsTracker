using System.Text;

namespace GpsTcpProxy;

public static class MqttLogger
{
    private static readonly object Lock = new();

    public static void LogInfo(string message) =>
        WriteLine($"[MQTT] {message}");

    public static void LogError(string message) =>
        WriteLine($"[MQTT ERROR] {message}");

    public static void LogPayload(string topic, ReadOnlyMemory<byte> payload)
    {
        var text = DecodePayload(payload);
        WriteLine($"[MQTT] json {topic}: {text}");
    }

    private static string DecodePayload(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return Encoding.UTF8.GetString(payload.Span);
        }
        catch
        {
            return Convert.ToHexString(payload.Span);
        }
    }

    private static void WriteLine(string message) =>
        TrafficLogger.LogInfo(message);
}
