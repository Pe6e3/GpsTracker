using System.Globalization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public static class TrackCreepHelper
{
    private const double ApproachMinPathMeters = 40;
    private const double ApproachMaxDurationMinutes = 180;

    public static bool IsSlowCreepRange(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex,
        TrackProcessingSettings settings)
    {
        var count = endIndex - startIndex + 1;
        if (count < settings.StationaryMinPoints)
            return false;

        var durationMinutes = (points[endIndex].GpsTimeUtc - points[startIndex].GpsTimeUtc).TotalMinutes;
        if (durationMinutes < settings.StationaryMinDurationMinutes)
            return false;

        if (!AllHaveCoordinates(points, startIndex, endIndex))
            return false;

        var totalDistanceMeters = ComputeTotalPathDistanceMeters(points, startIndex, endIndex);
        var maxRadiusMeters = ComputeMaxRadiusFromCenter(points, startIndex, endIndex);
        var maxCreepDistance = Math.Max(500, settings.StationaryRadiusMeters * 2);

        return totalDistanceMeters <= maxCreepDistance
            && maxRadiusMeters <= settings.StationaryRadiusMeters;
    }

    public static TrackPointDto[] CollapsePseudoMovingDisplayPoints(
        TrackPointDto[] points,
        TrackProcessingSettings settings)
    {
        if (points.Length < 2)
            return points;

        var result = new List<TrackPointDto>();
        var index = 0;

        while (index < points.Length)
        {
            if (!IsCollapsibleMovingPoint(points[index]))
            {
                result.Add(points[index]);
                index++;
                continue;
            }

            while (index < points.Length && IsCollapsibleMovingPoint(points[index]))
            {
                var cluster = TakeNextMovingCluster(points, ref index, settings);
                if (!TryCollapseMovingCluster(cluster, settings, out var collapsed))
                {
                    result.AddRange(cluster);
                    continue;
                }

                result.AddRange(collapsed);
            }
        }

        return MergeAdjacentStationaryDisplay(result).ToArray();
    }

    public static List<ProcessedTrackPoint> CollapsePseudoMovingProcessedPoints(
        IReadOnlyList<ProcessedTrackPoint> points,
        TrackProcessingSettings settings)
    {
        if (points.Count < 2)
            return points.ToList();

        var deviceId = points[0].DeviceId;
        var mapped = points.Select(MapProcessedToDisplay).ToArray();
        var collapsed = CollapsePseudoMovingDisplayPoints(mapped, settings);
        return collapsed.Select(point => MapDisplayToProcessed(point, deviceId)).ToList();
    }

    private static bool TryCollapseMovingCluster(
        IReadOnlyList<TrackPointDto> cluster,
        TrackProcessingSettings settings,
        out TrackPointDto[] replacement)
    {
        replacement = Array.Empty<TrackPointDto>();

        if (!IsPseudoMovingCluster(cluster, settings, out var centerLat, out var centerLon))
            return false;

        var first = cluster[0];
        var last = cluster[^1];
        var pathMeters = ComputeDisplayPathDistanceMeters(cluster);
        var durationMinutes = GetDisplayDurationMinutes(first, last);

        if (pathMeters >= ApproachMinPathMeters &&
            durationMinutes <= ApproachMaxDurationMinutes &&
            durationMinutes >= settings.StationaryMinDurationMinutes)
        {
            replacement =
            [
                CloneWithPointType(first, TrackPointType.UnknownGapStart),
                CreateGapEndPoint(centerLat, centerLon, last, TrackPointType.UnknownGapEnd),
                CreateStationaryPoint(centerLat, centerLon, first, TrackPointType.StationaryStart),
                CreateStationaryPoint(centerLat, centerLon, last, TrackPointType.StationaryEnd)
            ];
            return true;
        }

        replacement =
        [
            CreateStationaryPoint(centerLat, centerLon, first, TrackPointType.StationaryStart),
            CreateStationaryPoint(centerLat, centerLon, last, TrackPointType.StationaryEnd)
        ];
        return true;
    }

    private static bool IsPseudoMovingCluster(
        IReadOnlyList<TrackPointDto> cluster,
        TrackProcessingSettings settings,
        out double centerLat,
        out double centerLon)
    {
        centerLat = 0;
        centerLon = 0;

        if (cluster.Count < 2)
            return false;

        if (cluster.Any(point => !point.Lat.HasValue || !point.Lon.HasValue))
            return false;

        if (cluster.Any(point => point.Speed > settings.BriefStopMaxPeakSpeedKmh))
            return false;

        var durationMinutes = GetDisplayDurationMinutes(cluster[0], cluster[^1]);
        if (durationMinutes < settings.StationaryMinDurationMinutes)
            return false;

        var pathMeters = ComputeDisplayPathDistanceMeters(cluster);
        if (durationMinutes > 0)
        {
            var averageSpeedKmh = pathMeters / 1000d / (durationMinutes / 60d);
            if (averageSpeedKmh > settings.BriefStopMaxAverageSpeedKmh)
                return false;
        }

        var clusterCenterLat = cluster.Average(point => point.Lat!.Value);
        var clusterCenterLon = cluster.Average(point => point.Lon!.Value);
        centerLat = clusterCenterLat;
        centerLon = clusterCenterLon;

        var maxRadiusMeters = cluster.Max(point =>
            GeoDistance.HaversineMeters(
                clusterCenterLat,
                clusterCenterLon,
                point.Lat!.Value,
                point.Lon!.Value));

        var totalPathMeters = ComputeDisplayPathDistanceMeters(cluster);
        var maxCreepDistance = Math.Max(500, settings.StationaryRadiusMeters * 2);

        if (maxRadiusMeters > settings.StationaryRadiusMeters)
            return false;

        return totalPathMeters <= maxCreepDistance;
    }

    private static List<TrackPointDto> MergeAdjacentStationaryDisplay(List<TrackPointDto> points)
    {
        if (points.Count < 2)
            return points;

        var merged = new List<TrackPointDto>();
        var index = 0;

        while (index < points.Count)
        {
            if (points[index].PointType != TrackPointType.StationaryStart)
            {
                merged.Add(points[index]);
                index++;
                continue;
            }

            if (index + 1 >= points.Count || points[index + 1].PointType != TrackPointType.StationaryEnd)
            {
                merged.Add(points[index]);
                index++;
                continue;
            }

            var start = points[index];
            var end = points[index + 1];
            index += 2;

            while (index + 1 < points.Count &&
                   points[index].PointType == TrackPointType.StationaryStart &&
                   points[index + 1].PointType == TrackPointType.StationaryEnd &&
                   IsSamePlace(start, points[index]))
            {
                if (string.Compare(points[index + 1].TimeUtc, end.TimeUtc, StringComparison.Ordinal) > 0)
                    end = points[index + 1];

                index += 2;
            }

            merged.Add(start);
            merged.Add(end);
        }

        return merged;
    }

    private static bool IsCollapsibleMovingPoint(TrackPointDto point) =>
        point.PointType is TrackPointType.Moving or TrackPointType.RawValid or TrackPointType.Heartbeat;

    private static TrackPointDto[] TakeNextMovingCluster(
        TrackPointDto[] points,
        ref int index,
        TrackProcessingSettings settings)
    {
        var splitDistanceMeters = Math.Max(120, settings.StationaryRadiusMeters * 0.4);
        var cluster = new List<TrackPointDto> { points[index++] };

        while (index < points.Length && IsCollapsibleMovingPoint(points[index]))
        {
            var previous = cluster[^1];
            var next = points[index];
            if (TryGetDisplayDistanceMeters(previous, next, out var distanceMeters) &&
                distanceMeters > splitDistanceMeters)
                break;

            cluster.Add(points[index++]);
        }

        return cluster.ToArray();
    }

    private static bool TryGetDisplayDistanceMeters(
        TrackPointDto from,
        TrackPointDto to,
        out double distanceMeters)
    {
        distanceMeters = 0;
        if (!from.Lat.HasValue || !from.Lon.HasValue || !to.Lat.HasValue || !to.Lon.HasValue)
            return false;

        distanceMeters = GeoDistance.HaversineMeters(
            from.Lat.Value,
            from.Lon.Value,
            to.Lat.Value,
            to.Lon.Value);
        return true;
    }

    private static bool AllHaveCoordinates(IReadOnlyList<TelemetryPoint> points, int startIndex, int endIndex)
    {
        for (var index = startIndex; index <= endIndex; index++)
        {
            if (!points[index].Latitude.HasValue || !points[index].Longitude.HasValue)
                return false;
        }

        return true;
    }

    private static double ComputeTotalPathDistanceMeters(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex)
    {
        var total = 0d;

        for (var index = startIndex + 1; index <= endIndex; index++)
        {
            var previous = points[index - 1];
            var current = points[index];
            total += GeoDistance.HaversineMeters(
                previous.Latitude!.Value,
                previous.Longitude!.Value,
                current.Latitude!.Value,
                current.Longitude!.Value);
        }

        return total;
    }

    private static double ComputeMaxRadiusFromCenter(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex)
    {
        var centerLat = 0d;
        var centerLon = 0d;
        var count = endIndex - startIndex + 1;

        for (var index = startIndex; index <= endIndex; index++)
        {
            centerLat += points[index].Latitude!.Value;
            centerLon += points[index].Longitude!.Value;
        }

        centerLat /= count;
        centerLon /= count;

        var maxRadius = 0d;
        for (var index = startIndex; index <= endIndex; index++)
        {
            var radius = GeoDistance.HaversineMeters(
                centerLat,
                centerLon,
                points[index].Latitude!.Value,
                points[index].Longitude!.Value);
            if (radius > maxRadius)
                maxRadius = radius;
        }

        return maxRadius;
    }

    private static double ComputeDisplayPathDistanceMeters(IReadOnlyList<TrackPointDto> points)
    {
        var total = 0d;

        for (var index = 1; index < points.Count; index++)
        {
            var previous = points[index - 1];
            var current = points[index];
            if (!previous.Lat.HasValue || !previous.Lon.HasValue || !current.Lat.HasValue || !current.Lon.HasValue)
                continue;

            total += GeoDistance.HaversineMeters(
                previous.Lat.Value,
                previous.Lon.Value,
                current.Lat.Value,
                current.Lon.Value);
        }

        return total;
    }

    private static double GetDisplayDurationMinutes(TrackPointDto first, TrackPointDto last)
    {
        if (!TryParseDisplayTimeUtc(first.TimeUtc, out var startUtc) ||
            !TryParseDisplayTimeUtc(last.TimeUtc, out var endUtc))
            return 0;

        return Math.Max(0, (endUtc - startUtc).TotalMinutes);
    }

    private static TrackPointDto CreateStationaryPoint(
        double latitude,
        double longitude,
        TrackPointDto timeSource,
        string pointType) =>
        new()
        {
            Lat = latitude,
            Lon = longitude,
            Accuracy = timeSource.Accuracy,
            Alt = timeSource.Alt,
            Speed = 0,
            TimeUtc = timeSource.TimeUtc,
            TimeLocal = timeSource.TimeLocal,
            Geofences = timeSource.Geofences,
            PointType = pointType
        };

    private static TrackPointDto CreateGapEndPoint(
        double latitude,
        double longitude,
        TrackPointDto timeSource,
        string pointType) =>
        new()
        {
            Lat = latitude,
            Lon = longitude,
            Accuracy = timeSource.Accuracy,
            Alt = timeSource.Alt,
            Speed = 0,
            TimeUtc = timeSource.TimeUtc,
            TimeLocal = timeSource.TimeLocal,
            Geofences = timeSource.Geofences,
            PointType = pointType
        };

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

    private static bool IsSamePlace(TrackPointDto left, TrackPointDto right)
    {
        if (!left.Lat.HasValue || !left.Lon.HasValue || !right.Lat.HasValue || !right.Lon.HasValue)
            return false;

        return GeoDistance.HaversineMeters(left.Lat.Value, left.Lon.Value, right.Lat.Value, right.Lon.Value) <= 100;
    }

    private static bool TryParseDisplayTimeUtc(string timeUtc, out DateTime parsedUtc) =>
        DateTime.TryParse(
            timeUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsedUtc);

    private static TrackPointDto MapProcessedToDisplay(ProcessedTrackPoint point) =>
        new()
        {
            Lat = point.Latitude,
            Lon = point.Longitude,
            Alt = point.Altitude,
            Speed = point.SpeedKmh,
            Accuracy = point.Accuracy,
            TimeUtc = point.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            TimeLocal = AppTime.UtcToLocal(point.TimestampUtc).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            PointType = point.PointType,
            SourceRawTelemetryId = point.SourceRawTelemetryId,
            RawStartId = point.RawStartId,
            RawEndId = point.RawEndId
        };

    private static ProcessedTrackPoint MapDisplayToProcessed(TrackPointDto point, string deviceId) =>
        new()
        {
            DeviceId = deviceId,
            TimestampUtc = DateTime.Parse(point.TimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Latitude = point.Lat,
            Longitude = point.Lon,
            SpeedKmh = point.Speed,
            Altitude = point.Alt,
            Accuracy = point.Accuracy,
            PointType = point.PointType ?? TrackPointType.Moving,
            ProcessingBatchId = "creep-collapse",
            CreatedAtUtc = DateTime.UtcNow,
            SourceRawTelemetryId = point.SourceRawTelemetryId,
            RawStartId = point.RawStartId,
            RawEndId = point.RawEndId
        };
}
