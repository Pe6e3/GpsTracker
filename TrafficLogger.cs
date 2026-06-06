using System.Text;
using GpsTcpProxy.Models;
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

    public static void LogGt06DevicePacket(ProxyConnection connection, Gt06Message message)
    {
        connection.Touch();

        var line = Gt06EventFormatter.FormatLogLine(message);
        if (line == null)
            return;

        var deviceName = DeviceRegistry.GetDisplayName(
            string.IsNullOrWhiteSpace(message.Imei) ? connection.DeviceLabel : message.Imei);

        WriteLine($"[{Timestamp()}] [{deviceName}] {line}");
    }

    public static void LogUnparsedDeviceData(ProxyConnection connection, int bytes, DeviceProtocol? protocol = null)
    {
        connection.Touch();
        var deviceName = DeviceRegistry.GetDisplayName(connection.DeviceLabel);
        var protocolLabel = protocol switch
        {
            DeviceProtocol.Gt06 => "GT06",
            DeviceProtocol.Jt808 => "JT/T808",
            _ => "неизвестный"
        };
        WriteLine($"[{Timestamp()}] [{deviceName}] Тип пакета: не-{protocolLabel} bytes={bytes}");
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
