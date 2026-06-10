namespace GpsTcpProxy;

public static class MapLinkBuilder
{
    public static string BuildDeviceUrl(string mapBaseUrl, string deviceId) =>
        $"{mapBaseUrl.Trim().TrimEnd('/')}/map/{Uri.EscapeDataString(deviceId)}";
}
