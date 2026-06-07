namespace GpsTcpProxy;

public static class TelegramLogger
{
    public static void LogInfo(string message) =>
        TrafficLogger.LogInfo($"[TELEGRAM] {message}");

    public static void LogError(string message) =>
        TrafficLogger.LogInfo($"[TELEGRAM ERROR] {message}");
}
