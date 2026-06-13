using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public static class UnknownGapHelper
{
    public const double MinGapMinutes = 30;
    public const double MaxPreviousSpeedKmh = 1;
    public const double MinNextSpeedKmh = 2;

    public static bool IsUnknownGapPointType(string? pointType) =>
        pointType is TrackPointType.UnknownGapStart or TrackPointType.UnknownGapEnd;

    public static bool IsUnknownGapSegment(TrackPointDto? previous, TrackPointDto? next) =>
        IsUnknownGapPointType(previous?.PointType) || IsUnknownGapPointType(next?.PointType);

    public static bool ShouldCreateUnknownGap(TrackPointDto previous, TrackPointDto next, out double gapMinutes)
    {
        gapMinutes = 0;

        if (!TryParseTimeUtc(previous.TimeUtc, out var previousUtc) ||
            !TryParseTimeUtc(next.TimeUtc, out var nextUtc))
            return false;

        gapMinutes = (nextUtc - previousUtc).TotalMinutes;
        if (gapMinutes <= MinGapMinutes)
            return false;

        if (previous.Speed >= MaxPreviousSpeedKmh || next.Speed <= MinNextSpeedKmh)
            return false;

        return previous.Lat.HasValue && previous.Lon.HasValue;
    }

    public static TrackPointDto[] ApplyUnknownGaps(TrackPointDto[] points)
    {
        if (points.Length < 2)
            return points;

        var result = new List<TrackPointDto>(points.Length + 4);

        for (var index = 0; index < points.Length; index++)
        {
            var current = points[index];

            if (index < points.Length - 1 &&
                ShouldCreateUnknownGap(current, points[index + 1], out _))
            {
                result.Add(CloneWithPointType(current, TrackPointType.UnknownGapStart));
                result.Add(CreateUnknownGapEnd(current, points[index + 1]));
                continue;
            }

            result.Add(current);
        }

        return result.ToArray();
    }

    private static TrackPointDto CreateUnknownGapEnd(TrackPointDto anchor, TrackPointDto next)
    {
        var useNextCoordinates = next.Lat.HasValue && next.Lon.HasValue &&
            (!anchor.Lat.HasValue || !anchor.Lon.HasValue ||
             GeoDistance.HaversineMeters(
                 anchor.Lat.Value,
                 anchor.Lon.Value,
                 next.Lat.Value,
                 next.Lon.Value) > 50);

        return new()
        {
            Lat = useNextCoordinates ? next.Lat : anchor.Lat,
            Lon = useNextCoordinates ? next.Lon : anchor.Lon,
            Accuracy = useNextCoordinates ? next.Accuracy : anchor.Accuracy,
            Alt = useNextCoordinates ? next.Alt : anchor.Alt,
            Speed = 0,
            TimeUtc = next.TimeUtc,
            TimeLocal = next.TimeLocal,
            Geofences = useNextCoordinates ? next.Geofences : anchor.Geofences,
            PointType = TrackPointType.UnknownGapEnd
        };
    }

    private static TrackPointDto CloneWithPointType(TrackPointDto point, string pointType) =>
        new()
        {
            Lat = point.Lat,
            Lon = point.Lon,
            Accuracy = point.Accuracy,
            Alt = point.Alt,
            Speed = point.Speed,
            TimeUtc = point.TimeUtc,
            TimeLocal = point.TimeLocal,
            Geofences = point.Geofences,
            PointType = pointType
        };

    private static bool TryParseTimeUtc(string timeUtc, out DateTime parsedUtc) =>
        DateTime.TryParse(
            timeUtc,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out parsedUtc);
}
