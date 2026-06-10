using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public static class TrackSpeedHelper
{
    private const double MinTimeDeltaSeconds = 1;
    private const double ReportedSpeedMarginKmh = 10;
    private const double ReportedSpeedRatio = 1.35;
    private const double ReportedSpeedRatioOffsetKmh = 8;

    public static double ResolveDisplaySpeedKmh(
        TelemetryPoint? previous,
        TelemetryPoint current,
        TelemetryPoint? next,
        DeviceProtocol protocol,
        TrackProcessingSettings settings)
    {
        var maxSpeed = GetMaxSpeed(protocol, settings);
        var reported = Math.Max(0, current.SpeedKmh);
        var fromPrevious = TryCalculateSpeedKmh(previous, current);
        var fromNext = TryCalculateSpeedKmh(current, next);
        var derived = CombineDerivedSpeed(fromPrevious, fromNext);

        if (!derived.HasValue)
            return Math.Min(reported, maxSpeed);

        var derivedClamped = Math.Min(Math.Max(0, derived.Value), maxSpeed);

        if (reported <= derivedClamped + ReportedSpeedMarginKmh)
            return Math.Min(reported, maxSpeed);

        if (reported > derivedClamped * ReportedSpeedRatio + ReportedSpeedRatioOffsetKmh)
            return derivedClamped;

        return Math.Min(reported, maxSpeed);
    }

    public static double ResolveDisplaySpeedKmh(
        TrackPointDto? previous,
        TrackPointDto current,
        TrackPointDto? next,
        DeviceProtocol protocol,
        TrackProcessingSettings settings)
    {
        if (IsStationaryPointType(current.PointType))
            return 0;

        var maxSpeed = GetMaxSpeed(protocol, settings);
        var reported = Math.Max(0, current.Speed);
        var fromPrevious = TryCalculateSpeedKmh(previous, current);
        var fromNext = TryCalculateSpeedKmh(current, next);
        var derived = CombineDerivedSpeed(fromPrevious, fromNext);

        if (!derived.HasValue)
            return Math.Min(reported, maxSpeed);

        var derivedClamped = Math.Min(Math.Max(0, derived.Value), maxSpeed);

        if (reported <= derivedClamped + ReportedSpeedMarginKmh)
            return Math.Min(reported, maxSpeed);

        if (reported > derivedClamped * ReportedSpeedRatio + ReportedSpeedRatioOffsetKmh)
            return derivedClamped;

        return Math.Min(reported, maxSpeed);
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

    private static double GetMaxSpeed(DeviceProtocol protocol, TrackProcessingSettings settings) =>
        protocol == DeviceProtocol.OwnTracks
            ? settings.PhoneMaxSpeedKmh
            : settings.VehicleMaxSpeedKmh;
}
