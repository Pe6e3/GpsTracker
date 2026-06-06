using System.Globalization;
using System.Text;

namespace GpsTcpProxy.Protocol;

public static class Jt808EventFormatter
{
    public static string? FormatLogLine(Jt808Message message)
    {
        if (message.MessageId == Jt808Parser.MsgHeartbeat)
            return null;

        return message.MessageId switch
        {
            Jt808Parser.MsgLocationReport => FormatTelemetry(message),
            Jt808Parser.MsgAuthentication => FormatAuthenticationLine(message),
            Jt808Parser.MsgRegistration => FormatRegistrationLine(message),
            _ => $"Тип пакета: {Jt808PacketTypes.GetName(message.MessageId)} {FormatGenericDetails(message)}".TrimEnd()
        };
    }

    private static string FormatTelemetry(Jt808Message message)
    {
        var location = Jt808Parser.ParseLocationReport(message.Body);
        if (location == null)
            return "📡 parse_error";

        var gpsTimeText = location.Time;
        var timeDiff = string.Empty;
        var nowLocal = AppTime.NowLocal();

        if (message.Body.Length >= 28 && Jt808Bcd.TryParseDateTime(message.Body.AsSpan(22, 6), out var deviceTime))
        {
            var localTime = AppTime.DeviceTimeToLocal(deviceTime);
            gpsTimeText = localTime.ToString("dd.MM.yy HH:mm:ss");
            timeDiff = $" {FormatTimeDiff(localTime - nowLocal)}";
        }
        else if (DateTime.TryParseExact(location.Time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out deviceTime))
        {
            var localTime = AppTime.DeviceTimeToLocal(deviceTime);
            gpsTimeText = localTime.ToString("dd.MM.yy HH:mm:ss");
            timeDiff = $" {FormatTimeDiff(localTime - nowLocal)}";
        }

        return $"📡 {location.Latitude:F2}:{location.Longitude:F2}, {location.Altitude}m, {location.SpeedKmh:F1}км/ч  {gpsTimeText}{timeDiff}";
    }

    private static string FormatAuthenticationLine(Jt808Message message)
    {
        if (message.Body.Length == 0)
            return "Тип пакета: аутентификация";

        var token = message.Body.Length >= 6
            ? Jt808Bcd.DecodeDigits(message.Body.AsSpan(0, 6))
            : Jt808Bcd.DecodeDigits(message.Body);

        if (string.IsNullOrEmpty(token))
            return "Тип пакета: аутентификация";

        return $"Тип пакета: аутентификация token={token}";
    }

    private static string FormatRegistrationLine(Jt808Message message)
    {
        if (message.Body.Length == 0)
            return "Тип пакета: регистрация";

        return $"Тип пакета: регистрация data={FormatHex(message.Body)}";
    }

    private static string FormatGenericDetails(Jt808Message message)
    {
        if (!message.ChecksumValid)
            return "checksum=INVALID";

        if (message.Body.Length == 0)
            return string.Empty;

        return $"data={FormatHex(message.Body)}";
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

    private static string FormatHex(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

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
