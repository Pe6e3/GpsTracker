using System.Text;

namespace GpsTcpProxy;

public static class RawDataLogger
{
    private const string ServerIcon = "🖥️";
    private static readonly object Lock = new();
    private static readonly string LogsDirectory = Path.Combine(AppContext.BaseDirectory, "logs", "raw_data");

    public static void LogPacket(ProxyConnection connection, string direction, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("dd.MM.yy HH:mm:ss"))
            .Append(" #").Append(connection.ConnectionId)
            .Append(' ')
            .Append(FormatFlow(DeviceRegistry.GetDisplayName(connection.DeviceLabel), direction))
            .Append(' ')
            .Append(FormatHex(data))
            .AppendLine()
            .ToString();

        lock (Lock)
        {
            Directory.CreateDirectory(LogsDirectory);
            var logPath = Path.Combine(LogsDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(logPath, line, Encoding.UTF8);
        }
    }

    private static string FormatFlow(string deviceName, string direction)
    {
        var device = deviceName == "-" ? "?" : deviceName;

        return direction switch
        {
            "OUT" => $"[{device} → {ServerIcon}]",
            "IN" => $"[{ServerIcon} → {device}]",
            _ => $"[{direction}]"
        };
    }

    private static string FormatHex(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }

        return sb.ToString();
    }
}
