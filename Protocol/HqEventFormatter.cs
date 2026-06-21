using System.Globalization;

namespace GpsTcpProxy.Protocol;

public static class HqEventFormatter
{
    public static string? FormatLogLine(HqMessage message)
    {
        if (message.IsBinary)
            return message.Location == null
                ? $"binary {message.RawFrame.Length}b"
                : FormatLocation(message.Location);

        return message.PacketType switch
        {
            HqParser.PacketTypeTelemetry => message.Location == null
                ? $"V1 parse_error"
                : FormatLocation(message.Location),
            _ => $"тип={message.PacketType}"
        };
    }

    private static string FormatLocation(HqLocationReport location)
    {
        var localTime = AppTime.UtcToLocal(location.DeviceTimeUtc);
        var timeDiff = FormatTimeDiff(localTime - AppTime.NowLocal());

        return $"📡 {location.Latitude:F2}:{location.Longitude:F2}, 0m, {location.SpeedKmh:F1}км/ч  {localTime:dd.MM.yy HH:mm:ss} {timeDiff}";
    }

    private static string FormatTimeDiff(TimeSpan diff)
    {
        var sign = diff < TimeSpan.Zero ? "-" : "+";
        var totalSeconds = (long)Math.Abs(diff.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return $"({sign}{hours:D2}:{minutes:D2}:{seconds:D2})";
    }
}
