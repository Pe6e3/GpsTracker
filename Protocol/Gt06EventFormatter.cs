using System.Globalization;

namespace GpsTcpProxy.Protocol;

public static class Gt06EventFormatter
{
    public static string? FormatLogLine(Gt06Message message)
    {
        return message.ProtocolNumber switch
        {
            Gt06Parser.ProtocolLogin => FormatLogin(message),
            Gt06Parser.ProtocolLocation => FormatLocation(message),
            Gt06Parser.ProtocolHeartbeat => "heartbeat",
            Gt06Parser.ProtocolStatus => "status",
            _ => $"тип={Gt06Parser.GetProtocolName(message.ProtocolNumber)}"
        };
    }

    private static string FormatLogin(Gt06Message message)
    {
        if (string.IsNullOrWhiteSpace(message.Imei))
            return "login";

        return $"login IMEI={message.Imei}";
    }

    private static string FormatLocation(Gt06Message message)
    {
        var location = Gt06Parser.ParseLocationReport(message.Payload);
        if (location == null)
            return "📡 parse_error";

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
