using System.Globalization;

namespace GpsTcpProxy;

public static class MapLinkBuilder
{
    public static string BuildDeviceUrl(string mapBaseUrl, string deviceId) =>
        $"{mapBaseUrl.Trim().TrimEnd('/')}/{Uri.EscapeDataString(deviceId)}";

    public static string BuildGoogleMapsUrl(double lat, double lon) =>
        $"https://www.google.com/maps?q={lat.ToString("F5", CultureInfo.InvariantCulture)},{lon.ToString("F5", CultureInfo.InvariantCulture)}";
}
