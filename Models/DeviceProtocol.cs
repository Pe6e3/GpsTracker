namespace GpsTcpProxy.Models;

public enum DeviceProtocol
{
    Jt808,
    Gt06,
    OwnTracks
}

public static class DeviceProtocolParser
{
    public static bool TryParse(string? value, out DeviceProtocol protocol)
    {
        protocol = DeviceProtocol.Jt808;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().Replace("-", "/").Replace("_", "/").ToUpperInvariant();
        switch (normalized)
        {
            case "JT/T808":
            case "JT808":
            case "808":
                protocol = DeviceProtocol.Jt808;
                return true;
            case "GT06":
            case "GT06M":
                protocol = DeviceProtocol.Gt06;
                return true;
            case "OWNTRACKS":
            case "OWNTRACKS/MQTT":
            case "MQTT":
                protocol = DeviceProtocol.OwnTracks;
                return true;
            default:
                return false;
        }
    }

    public static string ToConfigValue(DeviceProtocol protocol) =>
        protocol switch
        {
            DeviceProtocol.Gt06 => "GT06",
            DeviceProtocol.OwnTracks => "OwnTracks",
            _ => "JT/T808"
        };
}
