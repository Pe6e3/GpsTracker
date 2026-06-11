using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public static class TrackSpeedHelper
{
    private const double MinTimeDeltaSeconds = 1;

    public static IReadOnlyList<TelemetryPoint> ApplyDerivedSpeed(IReadOnlyList<TelemetryPoint> points)
    {
        if (points.Count == 0)
            return points;

        var result = new TelemetryPoint[points.Count];

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            result[index] = WithSpeedKmh(
                point,
                CalculateSpeedKmh(
                    FindPreviousWithCoordinates(points, index),
                    point,
                    FindNextWithCoordinates(points, index)));
        }

        return result;
    }

    public static TrackPointDto[] ApplyDerivedSpeed(TrackPointDto[] points)
    {
        if (points.Length == 0)
            return points;

        var result = new TrackPointDto[points.Length];

        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            result[index] = WithSpeed(
                point,
                CalculateSpeedKmh(
                    FindPreviousWithCoordinates(points, index),
                    point,
                    FindNextWithCoordinates(points, index)));
        }

        return result;
    }

    public static double CalculateSpeedKmh(
        TrackPointDto? previous,
        TrackPointDto current,
        TrackPointDto? next)
    {
        if (IsStationaryPointType(current.PointType))
            return 0;

        if (UnknownGapHelper.IsUnknownGapPointType(current.PointType))
            return 0;

        if (UnknownGapHelper.IsUnknownGapPointType(previous?.PointType))
            return CalculateSpeedKmhFromNeighbors(
                null,
                TryCalculateSpeedKmh(current, next));

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

    public static double CalculateApproachSpeedKmh(
        TelemetryPoint? previous,
        TelemetryPoint current) =>
        TryCalculateSpeedKmh(previous, current) ?? 0;

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
            or TrackPointType.Heartbeat
            or TrackPointType.UnknownGapStart
            or TrackPointType.UnknownGapEnd;

    private static double? CombineDerivedSpeed(double? fromPrevious, double? fromNext)
    {
        if (fromPrevious.HasValue && fromNext.HasValue)
            return (fromPrevious.Value + fromNext.Value) / 2d;

        return fromPrevious ?? fromNext;
    }

    public static TelemetryPoint? FindPreviousWithCoordinates(IReadOnlyList<TelemetryPoint> points, int index)
    {
        for (var candidateIndex = index - 1; candidateIndex >= 0; candidateIndex--)
        {
            var candidate = points[candidateIndex];
            if (HasCoordinates(candidate))
                return candidate;
        }

        return null;
    }

    private static TelemetryPoint? FindNextWithCoordinates(IReadOnlyList<TelemetryPoint> points, int index)
    {
        for (var candidateIndex = index + 1; candidateIndex < points.Count; candidateIndex++)
        {
            var candidate = points[candidateIndex];
            if (HasCoordinates(candidate))
                return candidate;
        }

        return null;
    }

    private static TrackPointDto? FindPreviousWithCoordinates(IReadOnlyList<TrackPointDto> points, int index)
    {
        for (var candidateIndex = index - 1; candidateIndex >= 0; candidateIndex--)
        {
            var candidate = points[candidateIndex];
            if (HasCoordinates(candidate))
                return candidate;
        }

        return null;
    }

    private static TrackPointDto? FindNextWithCoordinates(IReadOnlyList<TrackPointDto> points, int index)
    {
        for (var candidateIndex = index + 1; candidateIndex < points.Count; candidateIndex++)
        {
            var candidate = points[candidateIndex];
            if (HasCoordinates(candidate))
                return candidate;
        }

        return null;
    }

    private static bool HasCoordinates(TelemetryPoint point) =>
        point.Latitude.HasValue && point.Longitude.HasValue;

    private static bool HasCoordinates(TrackPointDto point) =>
        point.Lat.HasValue && point.Lon.HasValue;

    private static TelemetryPoint WithSpeedKmh(TelemetryPoint point, double speedKmh) =>
        new()
        {
            Id = point.Id,
            DeviceId = point.DeviceId,
            DeviceName = point.DeviceName,
            Latitude = point.Latitude,
            Longitude = point.Longitude,
            Accuracy = point.Accuracy,
            Altitude = point.Altitude,
            SpeedKmh = speedKmh,
            Direction = point.Direction,
            GpsTimeUtc = point.GpsTimeUtc,
            ReceivedAtUtc = point.ReceivedAtUtc,
            Battery = point.Battery,
            Geofences = point.Geofences
        };

    private static TrackPointDto WithSpeed(TrackPointDto point, double speed) =>
        new()
        {
            Lat = point.Lat,
            Lon = point.Lon,
            Accuracy = point.Accuracy,
            Alt = point.Alt,
            Speed = speed,
            TimeUtc = point.TimeUtc,
            TimeLocal = point.TimeLocal,
            Geofences = point.Geofences,
            PointType = point.PointType
        };

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

        if (UnknownGapHelper.IsUnknownGapPointType(from.PointType))
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
