namespace GpsTcpProxy.Services;

internal static class TrackTimeHelper
{
    private const double StaleGpsMinutes = 10;

    public static DateTime ResolveDisplayTimeUtc(DateTime gpsTimeUtc, DateTime? receivedAtUtc)
    {
        if (!receivedAtUtc.HasValue)
            return gpsTimeUtc;

        if ((receivedAtUtc.Value - gpsTimeUtc).TotalMinutes <= StaleGpsMinutes)
            return gpsTimeUtc;

        return receivedAtUtc.Value;
    }
}
