using System.Text;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public static class TrafficLogger
{
    private static readonly object Lock = new();
    private static readonly string LogsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    public static void LogInfo(string message) =>
        WriteLine($"[{Timestamp()}] {message}");

    public static void LogDevicePacket(ProxyConnection connection, Jt808Message message)
    {
        connection.Touch();

        var line = Jt808EventFormatter.FormatLogLine(message);
        if (line == null)
            return;

        var deviceName = DeviceRegistry.GetDisplayName(
            string.IsNullOrWhiteSpace(message.TerminalId) ? connection.DeviceLabel : message.TerminalId);

        WriteLine($"[{Timestamp()}] [{deviceName}] {line}");
    }

    public static void LogUnparsedDeviceData(ProxyConnection connection, int bytes)
    {
        connection.Touch();
        var deviceName = DeviceRegistry.GetDisplayName(connection.DeviceLabel);
        WriteLine($"[{Timestamp()}] [{deviceName}] Тип пакета: не-JT808 bytes={bytes}");
    }

    private static void WriteLine(string message) =>
        Write($"{message}{Environment.NewLine}");

    private static void Write(string message)
    {
        lock (Lock)
        {
            Console.Write(message);
            Directory.CreateDirectory(LogsDirectory);
            var logPath = Path.Combine(LogsDirectory, $"{AppTime.NowLocal():yyyy-MM-dd}.log");
            File.AppendAllText(logPath, message, Encoding.UTF8);
        }
    }

    private static string Timestamp() => AppTime.NowLocal().ToString("dd.MM.yyyy HH:mm:ss");
}
