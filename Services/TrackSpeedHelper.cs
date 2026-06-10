using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public static class TrackSpeedHelper
{
    private const double MinTimeDeltaSeconds = 1;

    public static double CalculateSpeedKmh(
        TrackPointDto? previous,
        TrackPointDto current,
        TrackPointDto? next)
    {
        if (IsStationaryPointType(current.PointType))
            return 0;

        return CalculateSpeedKmhFromNeighbors(
            TryCalculateSpeedKmh(previous, current),
            TryCalculateSpeedKmh(current, next));
    }

    public static double CalculateSpeedKmh(
        TelemetryPoint? previous,
        TelemetryPoint current,
        TelemetryPoint? next) =>
        CalculateSpeedKmhFromNeighbors(
            TryCalculateSpeedKmh(previous, current),
            TryCalculateSpeedKmh(current, next));

    private static double CalculateSpeedKmhFromNeighbors(double? fromPrevious, double? fromNext)
    {
        var derived = CombineDerivedSpeed(fromPrevious, fromNext);

        return derived.HasValue
            ? Math.Max(0, derived.Value)
            : 0;
    }

    private static bool IsStationaryPointType(string? pointType) =>
        pointType is TrackPointType.StationaryStart
            or TrackPointType.StationaryEnd
            or TrackPointType.Heartbeat;

    private static double? CombineDerivedSpeed(double? fromPrevious, double? fromNext)
    {
        if (fromPrevious.HasValue && fromNext.HasValue)
            return (fromPrevious.Value + fromNext.Value) / 2d;

        return fromPrevious ?? fromNext;
    }

    private static double? TryCalculateSpeedKmh(TelemetryPoint? from, TelemetryPoint? to)
    {
        if (from == null || to == null)
            return null;

        if (!from.Latitude.HasValue || !from.Longitude.HasValue ||
            !to.Latitude.HasValue || !to.Longitude.HasValue)
            return null;

        var timeDeltaSeconds = (to.GpsTimeUtc - from.GpsTimeUtc).TotalSeconds;
        if (timeDeltaSeconds < MinTimeDeltaSeconds)
            return null;

        var distanceKm = GeoDistance.HaversineKm(
            from.Latitude.Value,
            from.Longitude.Value,
            to.Latitude.Value,
            to.Longitude.Value);

        return distanceKm / (timeDeltaSeconds / 3600d);
    }

    private static double? TryCalculateSpeedKmh(TrackPointDto? from, TrackPointDto? to)
    {
        if (from == null || to == null)
            return null;

        if (!from.Lat.HasValue || !from.Lon.HasValue || !to.Lat.HasValue || !to.Lon.HasValue)
            return null;

        if (!DateTime.TryParse(from.TimeUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var fromTime) ||
            !DateTime.TryParse(to.TimeUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var toTime))
            return null;

        var timeDeltaSeconds = (toTime - fromTime).TotalSeconds;
        if (timeDeltaSeconds < MinTimeDeltaSeconds)
            return null;

        var distanceKm = GeoDistance.HaversineKm(
            from.Lat.Value,
            from.Lon.Value,
            to.Lat.Value,
            to.Lon.Value);

        return distanceKm / (timeDeltaSeconds / 3600d);
    }
}
