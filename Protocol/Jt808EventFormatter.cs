using System.Text;

namespace GpsTcpProxy.Protocol;

public static class Jt808EventFormatter
{
    public static string FormatDeviceLog(Jt808Message message)
    {
        var typeName = Jt808PacketTypes.GetName(message.MessageId);
        var details = FormatDetails(message);
        if (string.IsNullOrEmpty(details))
            return typeName;

        return $"{typeName} {details}";
    }

    private static string FormatDetails(Jt808Message message)
    {
        if (!message.ChecksumValid)
            return "checksum=INVALID";

        return message.MessageId switch
        {
            Jt808Parser.MsgAuthentication => FormatAuthentication(message.Body),
            Jt808Parser.MsgHeartbeat => string.Empty,
            Jt808Parser.MsgLocationReport => FormatLocation(message.Body),
            Jt808Parser.MsgRegistration => FormatRegistration(message.Body),
            _ => message.Body.Length > 0 ? $"data={FormatHex(message.Body)}" : string.Empty
        };
    }

    private static string FormatAuthentication(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
            return string.Empty;

        return $"token={Jt808Bcd.DecodeDigits(body)}";
    }

    private static string FormatRegistration(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
            return string.Empty;

        return $"data={FormatHex(body)}";
    }

    private static string FormatLocation(ReadOnlySpan<byte> body)
    {
        var location = Jt808Parser.ParseLocationReport(body);
        if (location == null)
            return "parse_error";

        var sb = new StringBuilder();
        sb.Append($"{location.Latitude:F6}, {location.Longitude:F6}");
        sb.Append($" alt={location.Altitude}m");
        sb.Append($" speed={location.SpeedKmh:F1}km/h");
        sb.Append($" dir={location.Direction}°");
        sb.Append($" time={location.Time}");

        if (location.AlarmFlags != 0)
            sb.Append($" alarm=0x{location.AlarmFlags:X8}");

        foreach (var tlv in location.Extra)
        {
            if (tlv.Id == 0xCC && tlv.Value.All(b => b is >= 32 and <= 126))
                sb.Append($" iccid={Encoding.ASCII.GetString(tlv.Value)}");
        }

        return sb.ToString();
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
