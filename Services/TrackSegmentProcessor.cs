using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public static class TrackSegmentProcessor
{
    private const double PreserveTimeGapMinutes = 5;
    private const double PreserveSpeedDeltaKmh = 15;
    private const double PreserveCourseDeltaDegrees = 30;

    public sealed class ProcessingResult
    {
        public required IReadOnlyList<ProcessedTrackPoint> TrackPoints { get; init; }
        public int StationarySegmentsCount { get; init; }
        public int MovingSegmentsCount { get; init; }
    }

    public static ProcessingResult Process(
        string deviceId,
        IReadOnlyList<TelemetryPoint> filteredPoints,
        TrackProcessingSettings settings,
        string batchId,
        DateTime createdAtUtc)
    {
        var points = filteredPoints
            .Where(p => p.Latitude.HasValue && p.Longitude.HasValue)
            .ToArray();

        if (points.Length == 0)
        {
            return new ProcessingResult
            {
                TrackPoints = Array.Empty<ProcessedTrackPoint>(),
                StationarySegmentsCount = 0,
                MovingSegmentsCount = 0
            };
        }

        if (CanUseWholeBatchStationary(points, settings)
            && IsStationaryRange(points, 0, points.Length - 1, settings)
            && !HasTrailingMovement(points, settings))
        {
            return new ProcessingResult
            {
                TrackPoints = CreateStationaryPoints(
                    deviceId,
                    points,
                    settings,
                    batchId,
                    createdAtUtc,
                    movementFollowsAfter: false),
                StationarySegmentsCount = 1,
                MovingSegmentsCount = 0
            };
        }

        var averageSpeed = points.Average(p => p.SpeedKmh);
        var hasStationarySegment = FindNextStationaryStart(points, 0, settings).HasValue
            || IsLikelyLongStationary(points, settings);

        if (averageSpeed > settings.StationaryMaxAverageSpeedKmh && !hasStationarySegment)
        {
            var fastPathPoints = CreateMovingRangePoints(
                deviceId,
                points,
                0,
                points.Length - 1,
                settings,
                batchId,
                createdAtUtc,
                out var fastMovingSegments);

            return new ProcessingResult
            {
                TrackPoints = fastPathPoints,
                StationarySegmentsCount = fastPathPoints.Count(p => p.PointType == TrackPointType.StationaryStart),
                MovingSegmentsCount = fastMovingSegments
            };
        }

        var output = new List<ProcessedTrackPoint>();
        var stationarySegments = 0;
        var movingSegments = 0;
        var index = 0;

        while (index < points.Length)
        {
            var stationaryEnd = FindStationaryEnd(points, index, settings);
            if (stationaryEnd.HasValue)
            {
                if (index > 0)
                    TryAddArrivalMovingPoint(output, points, index - 1, index, deviceId, batchId, createdAtUtc, settings);

                output.AddRange(CreateStationaryPoints(
                    deviceId,
                    SliceRange(points, index, stationaryEnd.Value),
                    settings,
                    batchId,
                    createdAtUtc,
                    MovementFollowsAfter(points, stationaryEnd.Value, settings)));
                stationarySegments++;
                index = stationaryEnd.Value + 1;
                continue;
            }

            var briefStopEnd = FindBriefStopEnd(points, index, settings);
            if (briefStopEnd.HasValue)
            {
                if (index > 0)
                    TryAddArrivalMovingPoint(output, points, index - 1, index, deviceId, batchId, createdAtUtc, settings);

                output.AddRange(CreateStationaryPoints(
                    deviceId,
                    SliceRange(points, index, briefStopEnd.Value),
                    settings,
                    batchId,
                    createdAtUtc,
                    MovementFollowsAfter(points, briefStopEnd.Value, settings)));
                stationarySegments++;
                index = briefStopEnd.Value + 1;
                continue;
            }

            var nextStationaryStart = FindNextStationaryStart(points, index + 1, settings);
            var nextBriefStopStart = FindNextBriefStopStart(points, index + 1, settings);
            var nextBoundary = MinNullableIndex(nextStationaryStart, nextBriefStopStart);
            var movingEnd = nextBoundary.HasValue ? nextBoundary.Value - 1 : points.Length - 1;

            if (IsStationaryRange(points, index, movingEnd, settings))
            {
                output.AddRange(CreateStationaryPoints(
                    deviceId,
                    SliceRange(points, index, movingEnd),
                    settings,
                    batchId,
                    createdAtUtc,
                    MovementFollowsAfter(points, movingEnd, settings)));
                stationarySegments++;
            }
            else
            {
                var beforeCount = output.Count;
                output.AddRange(CreateMovingRangePoints(
                    deviceId,
                    points,
                    index,
                    movingEnd,
                    settings,
                    batchId,
                    createdAtUtc,
                    out var rangeMovingSegments));
                movingSegments += rangeMovingSegments;
                stationarySegments += output
                    .Skip(beforeCount)
                    .Count(p => p.PointType == TrackPointType.StationaryStart);

                if (nextBoundary.HasValue)
                    TryAddArrivalMovingPoint(
                        output,
                        points,
                        movingEnd,
                        nextBoundary.Value,
                        deviceId,
                        batchId,
                        createdAtUtc,
                        settings);
            }

            index = movingEnd + 1;
        }

        var mergedOutput = MergeAdjacentStationary(output, settings);

        return new ProcessingResult
        {
            TrackPoints = mergedOutput,
            StationarySegmentsCount = mergedOutput.Count(p => p.PointType == TrackPointType.StationaryStart),
            MovingSegmentsCount = mergedOutput.Count(p => p.PointType == TrackPointType.Moving)
        };
    }

    private static List<ProcessedTrackPoint> MergeAdjacentStationary(
        IReadOnlyList<ProcessedTrackPoint> points,
        TrackProcessingSettings settings)
    {
        if (points.Count == 0)
            return [];

        var merged = new List<ProcessedTrackPoint>();
        var mergeGapMinutes = Math.Max(settings.StationaryMinDurationMinutes, 15);
        var index = 0;

        while (index < points.Count)
        {
            if (points[index].PointType != TrackPointType.StationaryStart)
            {
                merged.Add(points[index]);
                index++;
                continue;
            }

            if (!TryReadStationaryGroup(points, ref index, out var groupStart, out var groupEnd))
            {
                merged.Add(points[index - 1]);
                continue;
            }

            while (index < points.Count && points[index].PointType == TrackPointType.StationaryStart)
            {
                var gapMinutes = (points[index].TimestampUtc - groupEnd.TimestampUtc).TotalMinutes;
                if (gapMinutes > mergeGapMinutes)
                    break;

                if (HasMovingPointsBetween(points, groupEnd.TimestampUtc, points[index].TimestampUtc))
                    break;

                if (!TryReadStationaryGroup(points, ref index, out _, out var nextEnd))
                    break;

                groupEnd = MergeStationaryBounds(groupStart, groupEnd, nextEnd);
            }

            merged.AddRange(CreateStationaryPointsFromBounds(
                groupStart,
                groupEnd,
                settings,
                movementFollowsAfter: MovementFollowsAfterProcessed(points, index)));
        }

        return merged;
    }

    private static bool TryReadStationaryGroup(
        IReadOnlyList<ProcessedTrackPoint> points,
        ref int index,
        out ProcessedTrackPoint groupStart,
        out ProcessedTrackPoint groupEnd)
    {
        groupStart = default!;
        groupEnd = default!;

        if (index >= points.Count || points[index].PointType != TrackPointType.StationaryStart)
            return false;

        groupStart = points[index];
        index++;

        while (index < points.Count && points[index].PointType == TrackPointType.Heartbeat)
            index++;

        if (index >= points.Count || points[index].PointType != TrackPointType.StationaryEnd)
            return false;

        groupEnd = points[index];
        index++;
        return true;
    }

    private static bool HasMovingPointsBetween(
        IReadOnlyList<ProcessedTrackPoint> points,
        DateTime fromUtc,
        DateTime toUtc)
    {
        for (var index = 0; index < points.Count; index++)
        {
            if (points[index].PointType != TrackPointType.Moving)
                continue;

            if (points[index].TimestampUtc > fromUtc && points[index].TimestampUtc < toUtc)
                return true;
        }

        return false;
    }

    private static ProcessedTrackPoint MergeStationaryBounds(
        ProcessedTrackPoint groupStart,
        ProcessedTrackPoint groupEnd,
        ProcessedTrackPoint nextEnd) =>
        new()
        {
            DeviceId = groupStart.DeviceId,
            TimestampUtc = nextEnd.TimestampUtc,
            Latitude = groupStart.Latitude,
            Longitude = groupStart.Longitude,
            SpeedKmh = groupEnd.SpeedKmh,
            Course = groupEnd.Course,
            Altitude = groupEnd.Altitude,
            Accuracy = groupEnd.Accuracy,
            SourceRawTelemetryId = nextEnd.SourceRawTelemetryId,
            PointType = groupEnd.PointType,
            ProcessingBatchId = groupEnd.ProcessingBatchId,
            CreatedAtUtc = groupEnd.CreatedAtUtc,
            OriginalPointsCount = (groupStart.OriginalPointsCount ?? 0)
                + (groupEnd.OriginalPointsCount ?? 0)
                + (nextEnd.OriginalPointsCount ?? 0),
            RawStartId = groupStart.RawStartId,
            RawEndId = nextEnd.RawEndId
        };

    private static bool CanUseWholeBatchStationary(
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings)
    {
        if (points.Count < settings.StationaryMinPoints)
            return false;

        var durationHours = (points[^1].GpsTimeUtc - points[0].GpsTimeUtc).TotalHours;
        return durationHours <= 24;
    }

    private static bool HasTrailingMovement(
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings)
    {
        var scanWindow = Math.Min(points.Count, Math.Max(settings.StationaryMinPoints * 4, 20));
        var startIndex = Math.Max(0, points.Count - scanWindow);

        for (var index = startIndex; index <= points.Count - settings.StationaryMinPoints; index++)
        {
            if (FindBriefStopEnd(points, index, settings, points.Count - 1).HasValue)
                continue;

            if (!IsStationaryRange(points, index, points.Count - 1, settings))
                return true;
        }

        return false;
    }

    private static IReadOnlyList<TelemetryPoint> SliceRange(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex)
    {
        var slice = new TelemetryPoint[endIndex - startIndex + 1];
        for (var index = 0; index < slice.Length; index++)
            slice[index] = points[startIndex + index];

        return slice;
    }

    private static bool MovementFollowsAfter(
        IReadOnlyList<TelemetryPoint> points,
        int stationaryEndIndex,
        TrackProcessingSettings settings)
    {
        var nextIndex = stationaryEndIndex + 1;
        if (nextIndex >= points.Count)
            return false;

        return !IsStationaryRange(points, nextIndex, points.Count - 1, settings);
    }

    private static bool MovementFollowsAfterProcessed(IReadOnlyList<ProcessedTrackPoint> points, int index) =>
        index < points.Count && points[index].PointType == TrackPointType.Moving;

    private static void TryAddArrivalMovingPoint(
        List<ProcessedTrackPoint> output,
        IReadOnlyList<TelemetryPoint> points,
        int lastMovingIndex,
        int arrivalIndex,
        string deviceId,
        string batchId,
        DateTime createdAtUtc,
        TrackProcessingSettings settings)
    {
        if (lastMovingIndex < 0 || arrivalIndex >= points.Count || arrivalIndex <= lastMovingIndex)
            return;

        var previous = points[lastMovingIndex];
        var arrival = points[arrivalIndex];

        if (!HasCoordinates(previous, arrival))
            return;

        var distanceMeters = GeoDistance.HaversineMeters(
            previous.Latitude!.Value,
            previous.Longitude!.Value,
            arrival.Latitude!.Value,
            arrival.Longitude!.Value);

        if (distanceMeters < 50)
            return;

        if (output.Count > 0)
        {
            var lastOutput = output[^1];
            if (lastOutput.PointType == TrackPointType.Moving &&
                lastOutput.SourceRawTelemetryId == arrival.Id)
                return;
        }

        output.Add(CreatePoint(
            deviceId,
            arrival,
            arrival.Latitude,
            arrival.Longitude,
            TrackPointType.Moving,
            batchId,
            createdAtUtc,
            arrival.Id,
            null,
            null,
            null,
            speedKmh: TrackSpeedHelper.CalculateSpeedKmh(
                previous,
                arrival,
                arrivalIndex < points.Count - 1 ? points[arrivalIndex + 1] : null)));
    }

    private static (DateTime StartUtc, DateTime EndUtc) ResolveStationaryDisplayTimestamps(
        DateTime startUtc,
        DateTime endUtc,
        bool movementFollowsAfter)
    {
        if (movementFollowsAfter)
        {
            var startLocalDate = AppTime.UtcToLocal(startUtc).Date;
            var endLocalDate = AppTime.UtcToLocal(endUtc).Date;
            if (startLocalDate < endLocalDate)
            {
                var midnightUtc = AppTime.LocalToUtc(endLocalDate);
                if (midnightUtc <= endUtc)
                    return (midnightUtc, endUtc);
            }
        }

        return (startUtc, endUtc);
    }

    private static int? FindNextStationaryStart(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        TrackProcessingSettings settings)
    {
        for (var index = startIndex; index < points.Count; index++)
        {
            if (FindStationaryEnd(points, index, settings).HasValue)
                return index;
        }

        return null;
    }

    private static int? FindStationaryEnd(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        TrackProcessingSettings settings)
    {
        int? lastValidEnd = null;
        var firstPossibleEnd = startIndex + settings.StationaryMinPoints - 1;
        if (firstPossibleEnd >= points.Count)
            return null;

        for (var endIndex = firstPossibleEnd; endIndex < points.Count; endIndex++)
        {
            if (!IsStationaryRange(points, startIndex, endIndex, settings))
            {
                if (lastValidEnd.HasValue)
                    break;

                continue;
            }

            var segmentHours = (points[endIndex].GpsTimeUtc - points[startIndex].GpsTimeUtc).TotalHours;
            if (segmentHours > settings.MaxStationarySegmentHours)
            {
                if (lastValidEnd.HasValue)
                    break;

                continue;
            }

            lastValidEnd = endIndex;
        }

        return lastValidEnd;
    }

    private static int? FindNextBriefStopStart(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        TrackProcessingSettings settings,
        int? maxIndex = null)
    {
        var limit = maxIndex ?? points.Count - 1;
        for (var index = startIndex; index <= limit; index++)
        {
            if (FindBriefStopEnd(points, index, settings, limit).HasValue)
                return index;
        }

        return null;
    }

    private static int? FindBriefStopEnd(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        TrackProcessingSettings settings,
        int? maxIndex = null)
    {
        var upperBound = maxIndex ?? points.Count - 1;
        if (startIndex > upperBound)
            return null;

        int? lastValidEnd = null;
        var firstPossibleEnd = startIndex + settings.BriefStopMinPoints - 1;
        if (firstPossibleEnd > upperBound)
            return null;

        for (var endIndex = firstPossibleEnd; endIndex <= upperBound; endIndex++)
        {
            if (!IsBriefStopRange(points, startIndex, endIndex, settings))
            {
                if (lastValidEnd.HasValue)
                    break;

                continue;
            }

            lastValidEnd = endIndex;
        }

        return lastValidEnd;
    }

    private static bool IsBriefStopRange(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex,
        TrackProcessingSettings settings)
    {
        var count = endIndex - startIndex + 1;
        if (count < settings.BriefStopMinPoints)
            return false;

        var durationSeconds = (points[endIndex].GpsTimeUtc - points[startIndex].GpsTimeUtc).TotalSeconds;
        if (durationSeconds < settings.BriefStopMinDurationSeconds)
            return false;

        if (durationSeconds > settings.BriefStopMaxDurationMinutes * 60)
            return false;

        var speedSum = 0.0;
        var maxSpeed = 0.0;
        var fastPoints = 0;
        for (var index = startIndex; index <= endIndex; index++)
        {
            if (!points[index].Latitude.HasValue || !points[index].Longitude.HasValue)
                return false;

            var speed = points[index].SpeedKmh;
            speedSum += speed;
            if (speed > maxSpeed)
                maxSpeed = speed;
            if (speed > settings.BriefStopMaxAverageSpeedKmh)
                fastPoints++;
        }

        if (speedSum / count > settings.BriefStopMaxAverageSpeedKmh)
            return false;

        if (maxSpeed > settings.BriefStopMaxPeakSpeedKmh)
            return false;

        if (fastPoints >= settings.BriefStopMinPoints)
            return false;

        var center = ComputeStationaryCenter(points, startIndex, endIndex);
        var radiusMeters = ComputeStationaryRadius(points, startIndex, endIndex, center);

        return radiusMeters <= settings.BriefStopRadiusMeters;
    }

    private static int? MinNullableIndex(int? left, int? right)
    {
        if (!left.HasValue)
            return right;
        if (!right.HasValue)
            return left;

        return Math.Min(left.Value, right.Value);
    }

    private static IReadOnlyList<ProcessedTrackPoint> CreateMovingRangePoints(
        string deviceId,
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex,
        TrackProcessingSettings settings,
        string batchId,
        DateTime createdAtUtc,
        out int movingSegmentCount)
    {
        movingSegmentCount = 0;
        var output = new List<ProcessedTrackPoint>();
        if (startIndex > endIndex)
            return output;

        var index = startIndex;
        while (index <= endIndex)
        {
            var briefStopEnd = FindBriefStopEnd(points, index, settings, endIndex);
            if (briefStopEnd.HasValue)
            {
                output.AddRange(CreateStationaryPoints(
                    deviceId,
                    SliceRange(points, index, briefStopEnd.Value),
                    settings,
                    batchId,
                    createdAtUtc,
                    briefStopEnd.Value < endIndex || MovementFollowsAfter(points, briefStopEnd.Value, settings)));
                index = briefStopEnd.Value + 1;
                continue;
            }

            var nextBriefStopStart = FindNextBriefStopStart(points, index + 1, settings, endIndex);
            var sliceEnd = nextBriefStopStart.HasValue ? nextBriefStopStart.Value - 1 : endIndex;
            var movingSlice = SliceRange(points, index, sliceEnd);
            if (movingSlice.Count > 0)
            {
                output.AddRange(CreateMovingPoints(deviceId, movingSlice, settings, batchId, createdAtUtc));
                movingSegmentCount++;
            }

            index = sliceEnd + 1;
        }

        return output;
    }

    private static bool IsLikelyLongStationary(
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings)
    {
        if (points.Count < settings.StationaryMinPoints)
            return false;

        var durationMinutes = (points[^1].GpsTimeUtc - points[0].GpsTimeUtc).TotalMinutes;
        if (durationMinutes < settings.StationaryMinDurationMinutes)
            return false;

        var totalDistanceMeters = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            var previous = points[index - 1];
            var current = points[index];
            if (!HasCoordinates(previous, current))
                continue;

            totalDistanceMeters += GeoDistance.HaversineMeters(
                previous.Latitude!.Value,
                previous.Longitude!.Value,
                current.Latitude!.Value,
                current.Longitude!.Value);
        }

        return totalDistanceMeters <= Math.Max(500, settings.StationaryRadiusMeters * 4);
    }

    private static bool IsStationaryRange(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex,
        TrackProcessingSettings settings)
    {
        var count = endIndex - startIndex + 1;
        if (count < settings.StationaryMinPoints)
            return false;

        var duration = (points[endIndex].GpsTimeUtc - points[startIndex].GpsTimeUtc).TotalMinutes;
        if (duration < settings.StationaryMinDurationMinutes)
            return false;

        var speedSum = 0.0;
        var maxSpeed = 0.0;
        var fastPoints = 0;
        for (var index = startIndex; index <= endIndex; index++)
        {
            if (!points[index].Latitude.HasValue || !points[index].Longitude.HasValue)
                return false;

            var speed = points[index].SpeedKmh;
            speedSum += speed;
            if (speed > maxSpeed)
                maxSpeed = speed;
            if (speed > settings.StationaryMaxAverageSpeedKmh)
                fastPoints++;
        }

        if (speedSum / count > settings.StationaryMaxAverageSpeedKmh)
            return false;

        if (maxSpeed > settings.StationaryMaxPeakSpeedKmh)
            return false;

        if (fastPoints >= settings.StationaryMinPoints)
            return false;

        var center = ComputeStationaryCenter(points, startIndex, endIndex);
        var radiusMeters = ComputeStationaryRadius(points, startIndex, endIndex, center);

        return radiusMeters <= settings.StationaryRadiusMeters;
    }

    private static double ComputeStationaryRadius(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex,
        (double Latitude, double Longitude) center)
    {
        var count = endIndex - startIndex + 1;
        var distances = new double[count];
        var offset = 0;

        for (var index = startIndex; index <= endIndex; index++)
        {
            distances[offset++] = GeoDistance.HaversineMeters(
                points[index].Latitude!.Value,
                points[index].Longitude!.Value,
                center.Latitude,
                center.Longitude);
        }

        Array.Sort(distances);

        var keptCount = Math.Max(1, (int)Math.Ceiling(count * 0.9));
        return distances[keptCount - 1];
    }

    private static (double Latitude, double Longitude) ComputeStationaryCenter(
        IReadOnlyList<TelemetryPoint> points,
        int startIndex,
        int endIndex)
    {
        var count = endIndex - startIndex + 1;
        var latitudes = new double[count];
        var longitudes = new double[count];
        var offset = 0;

        for (var index = startIndex; index <= endIndex; index++)
        {
            latitudes[offset] = points[index].Latitude!.Value;
            longitudes[offset] = points[index].Longitude!.Value;
            offset++;
        }

        Array.Sort(latitudes);
        Array.Sort(longitudes);
        var medianLat = Median(latitudes);
        var medianLon = Median(longitudes);

        var ranked = new (TelemetryPoint Point, double Distance)[count];
        offset = 0;

        for (var index = startIndex; index <= endIndex; index++)
        {
            var point = points[index];
            ranked[offset++] = (
                point,
                GeoDistance.HaversineMeters(
                    point.Latitude!.Value,
                    point.Longitude!.Value,
                    medianLat,
                    medianLon));
        }

        Array.Sort(ranked, (left, right) => right.Distance.CompareTo(left.Distance));

        var removeCount = (int)Math.Floor(count * 0.1);
        var keptCount = Math.Max(1, count - removeCount);
        double latitudeSum = 0;
        double longitudeSum = 0;

        for (var index = removeCount; index < removeCount + keptCount; index++)
        {
            latitudeSum += ranked[index].Point.Latitude!.Value;
            longitudeSum += ranked[index].Point.Longitude!.Value;
        }

        return (latitudeSum / keptCount, longitudeSum / keptCount);
    }

    private static (double Latitude, double Longitude) ComputeStationaryCenter(IReadOnlyList<TelemetryPoint> points) =>
        ComputeStationaryCenter(points, 0, points.Count - 1);

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;

        if (values.Count % 2 == 1)
            return values[values.Count / 2];

        return (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2.0;
    }

    private static IReadOnlyList<ProcessedTrackPoint> CreateStationaryPoints(
        string deviceId,
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings,
        string batchId,
        DateTime createdAtUtc,
        bool movementFollowsAfter)
    {
        var center = ComputeStationaryCenter(points);
        var bounds = new ProcessedTrackPoint
        {
            DeviceId = deviceId,
            TimestampUtc = points[0].GpsTimeUtc,
            Latitude = center.Latitude,
            Longitude = center.Longitude,
            SpeedKmh = 0,
            Course = points[0].Direction,
            Altitude = points[0].Altitude,
            Accuracy = points[0].Accuracy,
            SourceRawTelemetryId = points[0].Id,
            PointType = TrackPointType.StationaryStart,
            ProcessingBatchId = batchId,
            CreatedAtUtc = createdAtUtc,
            OriginalPointsCount = points.Count,
            RawStartId = points[0].Id,
            RawEndId = points[^1].Id
        };

        var endBounds = new ProcessedTrackPoint
        {
            DeviceId = deviceId,
            TimestampUtc = points[^1].GpsTimeUtc,
            Latitude = center.Latitude,
            Longitude = center.Longitude,
            SpeedKmh = 0,
            Course = points[^1].Direction,
            Altitude = points[^1].Altitude,
            Accuracy = points[^1].Accuracy,
            SourceRawTelemetryId = points[^1].Id,
            PointType = TrackPointType.StationaryEnd,
            ProcessingBatchId = batchId,
            CreatedAtUtc = createdAtUtc,
            OriginalPointsCount = points.Count,
            RawStartId = points[0].Id,
            RawEndId = points[^1].Id
        };

        return CreateStationaryPointsFromBounds(bounds, endBounds, settings, points, movementFollowsAfter);
    }

    public static IReadOnlyList<ProcessedTrackPoint> CreateStationaryPointsFromBounds(
        ProcessedTrackPoint groupStart,
        ProcessedTrackPoint groupEnd,
        TrackProcessingSettings settings,
        IReadOnlyList<TelemetryPoint>? sourcePoints = null,
        bool movementFollowsAfter = false)
    {
        var (startUtc, endUtc) = ResolveStationaryDisplayTimestamps(
            groupStart.TimestampUtc,
            groupEnd.TimestampUtc,
            movementFollowsAfter);

        var (displayLatitude, displayLongitude) = ResolveStationaryDisplayCoordinates(
            groupStart,
            groupEnd,
            sourcePoints);

        return
        [
            BuildStationaryDisplayPoint(
                groupStart,
                startUtc,
                TrackPointType.StationaryStart,
                isEnd: false,
                displayLatitude,
                displayLongitude),
            BuildStationaryDisplayPoint(
                groupEnd,
                endUtc,
                TrackPointType.StationaryEnd,
                isEnd: true,
                displayLatitude,
                displayLongitude)
        ];
    }

    private static (double Latitude, double Longitude) ResolveStationaryDisplayCoordinates(
        ProcessedTrackPoint groupStart,
        ProcessedTrackPoint groupEnd,
        IReadOnlyList<TelemetryPoint>? sourcePoints)
    {
        if (sourcePoints is { Count: > 0 })
            return ComputeStationaryCenter(sourcePoints);

        var latitudeValues = new List<double>(2);
        var longitudeValues = new List<double>(2);

        if (groupStart.Latitude.HasValue && groupStart.Longitude.HasValue)
        {
            latitudeValues.Add(groupStart.Latitude.Value);
            longitudeValues.Add(groupStart.Longitude.Value);
        }

        if (groupEnd.Latitude.HasValue && groupEnd.Longitude.HasValue)
        {
            latitudeValues.Add(groupEnd.Latitude.Value);
            longitudeValues.Add(groupEnd.Longitude.Value);
        }

        if (latitudeValues.Count == 0)
            return (0, 0);

        return (latitudeValues.Average(), longitudeValues.Average());
    }

    private static ProcessedTrackPoint BuildStationaryDisplayPoint(
        ProcessedTrackPoint source,
        DateTime timestampUtc,
        string pointType,
        bool isEnd,
        double? displayLatitude = null,
        double? displayLongitude = null) =>
        new()
        {
            DeviceId = source.DeviceId,
            TimestampUtc = timestampUtc,
            Latitude = displayLatitude ?? source.Latitude,
            Longitude = displayLongitude ?? source.Longitude,
            SpeedKmh = 0,
            Course = source.Course,
            Altitude = source.Altitude,
            Accuracy = source.Accuracy,
            SourceRawTelemetryId = isEnd
                ? source.SourceRawTelemetryId ?? source.RawEndId
                : source.SourceRawTelemetryId ?? source.RawStartId,
            PointType = pointType,
            ProcessingBatchId = source.ProcessingBatchId,
            CreatedAtUtc = source.CreatedAtUtc,
            OriginalPointsCount = source.OriginalPointsCount,
            RawStartId = source.RawStartId,
            RawEndId = source.RawEndId
        };

    private static IReadOnlyList<ProcessedTrackPoint> CreateMovingPoints(
        string deviceId,
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings,
        string batchId,
        DateTime createdAtUtc)
    {
        var simplified = settings.DouglasPeuckerToleranceMeters > 0
            ? SimplifyMovingPoints(points, settings.DouglasPeuckerToleranceMeters)
            : points;

        var result = new List<ProcessedTrackPoint>(simplified.Count);

        for (var index = 0; index < simplified.Count; index++)
        {
            var point = simplified[index];
            var previous = index > 0 ? simplified[index - 1] : null;
            var next = index < simplified.Count - 1 ? simplified[index + 1] : null;
            var speedKmh = TrackSpeedHelper.CalculateSpeedKmh(previous, point, next);

            result.Add(CreatePoint(
                deviceId,
                point,
                point.Latitude,
                point.Longitude,
                TrackPointType.Moving,
                batchId,
                createdAtUtc,
                point.Id,
                null,
                null,
                null,
                speedKmh: speedKmh));
        }

        return result;
    }

    private static IReadOnlyList<TelemetryPoint> SimplifyMovingPoints(
        IReadOnlyList<TelemetryPoint> points,
        double toleranceMeters)
    {
        if (points.Count <= 2)
            return points;

        var preserved = new HashSet<int> { 0, points.Count - 1 };

        for (var index = 1; index < points.Count - 1; index++)
        {
            if (ShouldPreservePoint(points, index))
                preserved.Add(index);
        }

        var working = points
            .Select((point, index) => new IndexedPoint(index, point))
            .Where(x => preserved.Contains(x.Index))
            .ToArray();

        var simplifiedIndexes = DouglasPeucker(
            working,
            0,
            working.Length - 1,
            toleranceMeters,
            new HashSet<int>());

        return simplifiedIndexes
            .OrderBy(i => i)
            .Select(i => points[i])
            .ToArray();
    }

    private static bool ShouldPreservePoint(IReadOnlyList<TelemetryPoint> points, int index)
    {
        var current = points[index];
        var previous = points[index - 1];
        var next = points[index + 1];

        var timeGapMinutes = (current.GpsTimeUtc - previous.GpsTimeUtc).TotalMinutes;
        if (timeGapMinutes >= PreserveTimeGapMinutes)
            return true;

        if (Math.Abs(current.SpeedKmh - previous.SpeedKmh) >= PreserveSpeedDeltaKmh)
            return true;

        if (current.SpeedKmh <= 3 && previous.SpeedKmh > 5)
            return true;

        if (current.SpeedKmh <= 1)
            return true;

        if (HasCoordinates(previous, current, next))
        {
            var courseDelta = CourseDelta(previous.Direction, current.Direction);
            if (courseDelta >= PreserveCourseDeltaDegrees)
                return true;
        }

        return false;
    }

    private static double CourseDelta(int previousCourse, int currentCourse)
    {
        var delta = Math.Abs(currentCourse - previousCourse) % 360;
        return delta > 180 ? 360 - delta : delta;
    }

    private static HashSet<int> DouglasPeucker(
        IReadOnlyList<IndexedPoint> points,
        int start,
        int end,
        double toleranceMeters,
        HashSet<int> preservedIndexes)
    {
        if (end <= start + 1)
        {
            preservedIndexes.Add(points[start].Index);
            preservedIndexes.Add(points[end].Index);
            return preservedIndexes;
        }

        var maxDistance = 0.0;
        var maxIndex = start;

        var startPoint = points[start].Point;
        var endPoint = points[end].Point;

        for (var index = start + 1; index < end; index++)
        {
            var point = points[index].Point;
            var distance = GeoDistance.PerpendicularDistanceMeters(
                point.Latitude!.Value,
                point.Longitude!.Value,
                startPoint.Latitude!.Value,
                startPoint.Longitude!.Value,
                endPoint.Latitude!.Value,
                endPoint.Longitude!.Value);

            if (distance <= maxDistance)
                continue;

            maxDistance = distance;
            maxIndex = index;
        }

        if (maxDistance > toleranceMeters)
        {
            DouglasPeucker(points, start, maxIndex, toleranceMeters, preservedIndexes);
            DouglasPeucker(points, maxIndex, end, toleranceMeters, preservedIndexes);
            return preservedIndexes;
        }

        preservedIndexes.Add(points[start].Index);
        preservedIndexes.Add(points[end].Index);
        return preservedIndexes;
    }

    private static ProcessedTrackPoint CreatePoint(
        string deviceId,
        TelemetryPoint source,
        double? latitude,
        double? longitude,
        string pointType,
        string batchId,
        DateTime createdAtUtc,
        long sourceRawTelemetryId,
        int? originalPointsCount,
        long? rawStartId,
        long? rawEndId,
        DateTime? timestampUtc = null,
        double? speedKmh = null) =>
        new()
        {
            DeviceId = deviceId,
            TimestampUtc = timestampUtc ?? source.GpsTimeUtc,
            Latitude = latitude,
            Longitude = longitude,
            SpeedKmh = speedKmh ?? 0,
            Course = source.Direction,
            Altitude = source.Altitude,
            Accuracy = source.Accuracy,
            SourceRawTelemetryId = sourceRawTelemetryId,
            PointType = pointType,
            ProcessingBatchId = batchId,
            CreatedAtUtc = createdAtUtc,
            OriginalPointsCount = originalPointsCount,
            RawStartId = rawStartId,
            RawEndId = rawEndId
        };

    private static bool HasCoordinates(params TelemetryPoint[] points) =>
        points.All(p => p.Latitude.HasValue && p.Longitude.HasValue);

    private readonly record struct IndexedPoint(int Index, TelemetryPoint Point);
}
