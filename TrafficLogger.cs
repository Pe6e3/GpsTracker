using System.Text;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public static class TrafficLogger
{
    private static readonly object Lock = new();

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
            LogFiles.Traffic.Append(message);
        }
    }

    private static string Timestamp() => AppTime.NowLocal().ToString("dd.MM.yyyy HH:mm:ss");
}
