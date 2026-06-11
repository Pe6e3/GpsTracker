using System.Globalization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class TrackQueryService
{
    private const int MaxTrackPoints = 10000;

    private readonly TelemetryStore _telemetryStore;
    private readonly TrackPointStore _trackPointStore;
    private readonly TrackProcessingSettings _trackProcessingSettings;
    private readonly double _maxTrackAccuracyMeters;

    public TrackQueryService(
        TelemetryStore telemetryStore,
        TrackPointStore trackPointStore,
        MqttSettings mqttSettings,
        TrackProcessingSettings trackProcessingSettings)
    {
        _telemetryStore = telemetryStore;
        _trackPointStore = trackPointStore;
        _trackProcessingSettings = trackProcessingSettings;
        _maxTrackAccuracyMeters = mqttSettings.MaxTrackAccuracyMeters;
    }

    public TrackResponse? GetTrack(string deviceId, string? from, string? to, bool raw = false)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return null;

        var deviceName = DeviceRegistry.GetDisplayName(normalizedId);
        var (fromLocal, toLocal) = ResolveRange(from, to);
        var fromUtc = AppTime.LocalToUtc(fromLocal);
        var toUtc = AppTime.LocalToUtc(toLocal);
        var protocol = DeviceRegistry.GetProtocol(normalizedId);

        if (raw || !_trackProcessingSettings.Enabled)
        {
            var rawTrack = _telemetryStore.GetTrack(normalizedId, fromUtc, toUtc);
            var filteredRaw = TrackOutlierFilter.FilterForRuntime(
                rawTrack,
                _trackProcessingSettings,
                protocol);

            var rawDisplay = FilterDisplayOutliers(
                FilterDisplayPoints(filteredRaw.Select(MapRawPoint)),
                _trackProcessingSettings);
            rawDisplay = TrackSpeedHelper.ApplyDerivedSpeed(rawDisplay);
            rawDisplay = IncludeUnknownGapAnchor(rawDisplay, normalizedId, fromUtc);
            rawDisplay = UnknownGapHelper.ApplyUnknownGaps(rawDisplay);

            return new TrackResponse
            {
                DeviceId = normalizedId,
                DeviceName = deviceName,
                Points = TrackSpeedHelper.ApplyDerivedSpeed(
                    FilterToTimeRange(rawDisplay, fromUtc, toUtc, keepUnknownGapAnchors: true))
            };
        }

        var processedPoints = IncludePrecedingStationaryGroup(
            _trackPointStore.GetTrack(normalizedId, fromUtc, toUtc),
            normalizedId,
            fromUtc);
        var lastProcessedRawId = _trackPointStore.GetLastProcessedRawId(normalizedId);
        var freshRawPoints = _telemetryStore.GetRawTelemetryAfter(
            normalizedId,
            lastProcessedRawId,
            toUtc,
            limit: MaxTrackPoints,
            fromUtcMin: fromUtc);
        var filteredFreshRaw = TrackOutlierFilter.FilterForRuntime(
            freshRawPoints,
            _trackProcessingSettings,
            protocol);

        var freshInRange = filteredFreshRaw
            .Where(p => p.GpsTimeUtc >= fromUtc && p.GpsTimeUtc <= toUtc)
            .ToArray();

        var freshProcessed = ProcessFreshPointsForDisplay(
            normalizedId,
            freshInRange,
            lastProcessedRawId,
            fromUtc,
            toUtc,
            protocol);

        var merged = MergeDisplayPoints(
            processedPoints.Select(MapProcessedPoint),
            freshProcessed.Select(MapProcessedPoint));

        var filled = FillOptimizedGaps(
            normalizedId,
            FilterDisplayPoints(merged),
            protocol,
            fromUtc,
            toUtc);
        var cleaned = FilterDisplayOutliers(filled, _trackProcessingSettings);

        var optimizedDisplay = FilterToTimeRange(
            UnifyStationaryCoordinates(cleaned),
            fromUtc,
            toUtc);
        optimizedDisplay = TrackSpeedHelper.ApplyDerivedSpeed(optimizedDisplay);
        optimizedDisplay = IncludeUnknownGapAnchor(optimizedDisplay, normalizedId, fromUtc);
        optimizedDisplay = UnknownGapHelper.ApplyUnknownGaps(optimizedDisplay);

        return new TrackResponse
        {
            DeviceId = normalizedId,
            DeviceName = deviceName,
            Points = TrackSpeedHelper.ApplyDerivedSpeed(
                FilterToTimeRange(optimizedDisplay, fromUtc, toUtc, keepUnknownGapAnchors: true))
        };
    }

    private static TrackPointDto[] FilterDisplayOutliers(
        TrackPointDto[] points,
        TrackProcessingSettings settings)
    {
        if (points.Length < 3)
            return points;

        var keep = new bool[points.Length];
        Array.Fill(keep, true);

        for (var index = 1; index < points.Length - 1; index++)
        {
            if (!HasDisplayCoordinates(points[index - 1], points[index], points[index + 1]))
                continue;

            var distanceAb = DisplayDistanceMeters(points[index - 1], points[index]);
            var distanceBc = DisplayDistanceMeters(points[index], points[index + 1]);
            var distanceAc = DisplayDistanceMeters(points[index - 1], points[index + 1]);
            var timeAb = DisplayTimeDeltaSeconds(points[index - 1], points[index]);
            var timeBc = DisplayTimeDeltaSeconds(points[index], points[index + 1]);

            if (distanceAb > settings.JumpDistanceMeters &&
                distanceBc > settings.JumpDistanceMeters &&
                distanceAc < settings.ReturnDistanceMeters &&
                timeAb < settings.JumpTimeSeconds &&
                timeBc < settings.JumpTimeSeconds)
                keep[index] = false;
        }

        for (var index = 1; index < points.Length; index++)
        {
            if (!keep[index] || !HasDisplayCoordinates(points[index - 1], points[index]))
                continue;

            var distanceMeters = DisplayDistanceMeters(points[index - 1], points[index]);
            var timeSeconds = DisplayTimeDeltaSeconds(points[index - 1], points[index]);
            if (timeSeconds <= 0 || timeSeconds > settings.JumpTimeSeconds || distanceMeters <= settings.JumpDistanceMeters)
                continue;

            var speedKmh = distanceMeters / 1000d / (timeSeconds / 3600d);
            if (speedKmh <= 120)
                continue;

            for (var clusterIndex = index; clusterIndex < points.Length; clusterIndex++)
            {
                if (!keep[clusterIndex])
                    break;

                if (DisplayDistanceMeters(points[index], points[clusterIndex]) > settings.ReturnDistanceMeters)
                    break;

                keep[clusterIndex] = false;
            }
        }

        return RemoveStickyFalseClusters(
            points.Where((_, index) => keep[index]).ToArray(),
            settings);
    }

    private static TrackPointDto[] RemoveStickyFalseClusters(
        TrackPointDto[] points,
        TrackProcessingSettings settings)
    {
        if (points.Length < 3)
            return points;

        var keep = Enumerable.Repeat(true, points.Length).ToArray();
        var index = 0;

        while (index < points.Length)
        {
            var clusterEnd = index;
            while (clusterEnd + 1 < points.Length &&
                   HasDisplayCoordinates(points[index], points[clusterEnd + 1]) &&
                   DisplayDistanceMeters(points[index], points[clusterEnd + 1]) <= settings.ReturnDistanceMeters)
                clusterEnd++;

            if (clusterEnd > index && index > 0)
            {
                var clusterDurationSec = DisplayTimeDeltaSeconds(points[index], points[clusterEnd]);
                var offsetFromPrev = DisplayDistanceMeters(points[index - 1], points[index]);
                if (clusterDurationSec >= 300 &&
                    offsetFromPrev > settings.JumpDistanceMeters &&
                    clusterEnd < points.Length - 1 &&
                    DisplayDistanceMeters(points[clusterEnd], points[clusterEnd + 1]) > settings.ReturnDistanceMeters)
                {
                    for (var clusterIndex = index; clusterIndex <= clusterEnd; clusterIndex++)
                        keep[clusterIndex] = false;
                }
            }

            index = clusterEnd + 1;
        }

        return points.Where((_, clusterIndex) => keep[clusterIndex]).ToArray();
    }

    private static bool HasDisplayCoordinates(params TrackPointDto[] points) =>
        points.All(point => point.Lat.HasValue && point.Lon.HasValue);

    private static double DisplayDistanceMeters(TrackPointDto from, TrackPointDto to) =>
        GeoDistance.HaversineMeters(from.Lat!.Value, from.Lon!.Value, to.Lat!.Value, to.Lon!.Value);

    private static double DisplayTimeDeltaSeconds(TrackPointDto from, TrackPointDto to)
    {
        if (!TryParseDisplayTimeUtc(from.TimeUtc, out var fromUtc) ||
            !TryParseDisplayTimeUtc(to.TimeUtc, out var toUtc))
            return double.MaxValue;

        return Math.Max(0, (toUtc - fromUtc).TotalSeconds);
    }

    private TrackPointDto[] FillOptimizedGaps(
        string deviceId,
        TrackPointDto[] points,
        DeviceProtocol protocol,
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (points.Length < 2)
            return FilterToTimeRange(points, fromUtc, toUtc);

        var gapMinutes = Math.Max(_trackProcessingSettings.StationaryMinDurationMinutes, 15);
        var result = new List<TrackPointDto>();

        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            if (IsPointInTimeRange(point, fromUtc, toUtc) && (result.Count == 0 || !IsSameDisplayPoint(result[^1], point)))
                result.Add(point);

            if (index >= points.Length - 1)
                continue;

            var current = points[index];
            var next = points[index + 1];

            if (!TryGetGapMinutes(current, next, out var gap) || gap < gapMinutes)
                continue;

            if (!TryParseDisplayTimeUtc(next.TimeUtc, out var nextUtc) || nextUtc < fromUtc)
                continue;

            foreach (var fillPoint in BuildGapFillPoints(deviceId, current, next, protocol, fromUtc, toUtc))
            {
                if (result.Count > 0 && IsSameDisplayPoint(result[^1], fillPoint))
                    continue;

                result.Add(fillPoint);
            }
        }

        return FilterToTimeRange(result.ToArray(), fromUtc, toUtc);
    }

    private IReadOnlyList<TrackPointDto> BuildGapFillPoints(
        string deviceId,
        TrackPointDto gapStart,
        TrackPointDto gapEnd,
        DeviceProtocol protocol,
        DateTime fromUtc,
        DateTime toUtc)
    {
        if (!TryGetGapUtcRange(gapStart, gapEnd, out var startUtc, out var endUtc))
            return Array.Empty<TrackPointDto>();

        var rawPoints = _telemetryStore.GetTrack(
            deviceId,
            startUtc.AddSeconds(1),
            endUtc.AddSeconds(-1));

        if (rawPoints.Count < _trackProcessingSettings.StationaryMinPoints)
            return Array.Empty<TrackPointDto>();

        var filteredRaw = TrackSpeedHelper.ApplyDerivedSpeed(
            TrackOutlierFilter.FilterForRuntime(
                rawPoints,
                _trackProcessingSettings,
                protocol));

        if (filteredRaw.Count < _trackProcessingSettings.StationaryMinPoints)
            return Array.Empty<TrackPointDto>();

        var parkingStartIndex = FindParkingStartIndex(filteredRaw, _trackProcessingSettings);
        if (parkingStartIndex >= 0)
        {
            var fillPoints = new List<TrackPointDto>();

            if (parkingStartIndex > 0)
            {
                var approach = TrackSegmentProcessor.Process(
                    deviceId,
                    filteredRaw.Take(parkingStartIndex).ToArray(),
                    _trackProcessingSettings,
                    batchId: "gap-fill",
                    createdAtUtc: DateTime.UtcNow);

                fillPoints.AddRange(approach.TrackPoints
                    .Where(p => p.PointType == TrackPointType.Moving)
                    .Select(MapProcessedPoint)
                    .Where(p => p.Lat.HasValue && p.Lon.HasValue && IsPointInTimeRange(p, fromUtc, toUtc)));
            }

            var parking = TrackSegmentProcessor.Process(
                deviceId,
                filteredRaw.Skip(parkingStartIndex).ToArray(),
                _trackProcessingSettings,
                batchId: "gap-fill",
                createdAtUtc: DateTime.UtcNow);

            fillPoints.AddRange(parking.TrackPoints
                .Where(p => p.PointType is TrackPointType.StationaryStart or TrackPointType.StationaryEnd)
                .Select(MapProcessedPoint)
                .Where(p => p.Lat.HasValue && p.Lon.HasValue && IsPointInTimeRange(p, fromUtc, toUtc)));

            if (fillPoints.Count > 0)
                return fillPoints;
        }

        var processed = TrackSegmentProcessor.Process(
            deviceId,
            filteredRaw,
            _trackProcessingSettings,
            batchId: "gap-fill",
            createdAtUtc: DateTime.UtcNow);

        return processed.TrackPoints
            .Select(MapProcessedPoint)
            .Where(p => p.Lat.HasValue && p.Lon.HasValue && IsPointInTimeRange(p, fromUtc, toUtc))
            .ToArray();
    }

    private static int FindParkingStartIndex(
        IReadOnlyList<TelemetryPoint> points,
        TrackProcessingSettings settings)
    {
        var minPoints = settings.StationaryMinPoints;

        for (var index = 0; index <= points.Count - minPoints; index++)
        {
            var cluster = points.Skip(index).Take(minPoints).ToArray();
            if (cluster.Any(point => !point.Latitude.HasValue || !point.Longitude.HasValue))
                continue;

            var centerLat = cluster.Average(point => point.Latitude!.Value);
            var centerLon = cluster.Average(point => point.Longitude!.Value);
            var allClose = cluster.All(point =>
                GeoDistance.HaversineMeters(
                    centerLat,
                    centerLon,
                    point.Latitude!.Value,
                    point.Longitude!.Value) <= settings.StationaryRadiusMeters);
            var allSlow = cluster.All(point => point.SpeedKmh <= settings.StationaryMaxPeakSpeedKmh);

            if (!allClose || !allSlow)
                continue;

            var parkingDurationMinutes = (points[^1].GpsTimeUtc - cluster[0].GpsTimeUtc).TotalMinutes;
            if (parkingDurationMinutes >= settings.StationaryMinDurationMinutes)
                return index;
        }

        return -1;
    }

    private static bool TryGetGapMinutes(TrackPointDto left, TrackPointDto right, out double gapMinutes)
    {
        gapMinutes = 0;
        if (!TryGetGapUtcRange(left, right, out var startUtc, out var endUtc))
            return false;

        gapMinutes = (endUtc - startUtc).TotalMinutes;
        return gapMinutes > 0;
    }

    private static bool TryGetGapUtcRange(
        TrackPointDto left,
        TrackPointDto right,
        out DateTime startUtc,
        out DateTime endUtc)
    {
        startUtc = default;
        endUtc = default;

        if (!TryParseDisplayTimeUtc(left.TimeUtc, out startUtc) ||
            !TryParseDisplayTimeUtc(right.TimeUtc, out endUtc) ||
            endUtc <= startUtc)
            return false;

        return true;
    }

    private static bool TryParseDisplayTimeUtc(string timeUtc, out DateTime parsedUtc)
    {
        parsedUtc = default;
        if (string.IsNullOrWhiteSpace(timeUtc))
            return false;

        return DateTime.TryParse(
            timeUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsedUtc);
    }

    private static bool IsSameDisplayPoint(TrackPointDto left, TrackPointDto right)
    {
        if (!left.Lat.HasValue || !right.Lat.HasValue || !left.Lon.HasValue || !right.Lon.HasValue)
            return string.Equals(left.TimeUtc, right.TimeUtc, StringComparison.Ordinal);

        return string.Equals(left.TimeUtc, right.TimeUtc, StringComparison.Ordinal) &&
               GeoDistance.HaversineMeters(left.Lat.Value, left.Lon.Value, right.Lat.Value, right.Lon.Value) < 25;
    }

    private static TrackPointDto[] UnifyStationaryCoordinates(TrackPointDto[] points)
    {
        if (points.Length < 2)
            return points;

        var unified = new TrackPointDto[points.Length];

        for (var index = 0; index < points.Length; index++)
            unified[index] = points[index];

        for (var index = 0; index < points.Length - 1; index++)
        {
            var start = unified[index];
            var end = unified[index + 1];

            if (start.PointType != TrackPointType.StationaryStart ||
                end.PointType != TrackPointType.StationaryEnd ||
                start.Lat == null || start.Lon == null || end.Lat == null || end.Lon == null)
                continue;

            var latitude = (start.Lat.Value + end.Lat.Value) / 2d;
            var longitude = (start.Lon.Value + end.Lon.Value) / 2d;

            unified[index] = CloneTrackPoint(start, latitude, longitude);
            unified[index + 1] = CloneTrackPoint(end, latitude, longitude);
        }

        return unified;
    }

    private static TrackPointDto CloneTrackPoint(TrackPointDto point, double latitude, double longitude) =>
        new()
        {
            Lat = latitude,
            Lon = longitude,
            Accuracy = point.Accuracy,
            Alt = point.Alt,
            Speed = point.Speed,
            TimeUtc = point.TimeUtc,
            TimeLocal = point.TimeLocal,
            Geofences = point.Geofences,
            PointType = point.PointType
        };

    private static TrackPointDto[] MergeDisplayPoints(
        IEnumerable<TrackPointDto> processed,
        IEnumerable<TrackPointDto> fresh)
    {
        var list = processed.OrderBy(p => p.TimeUtc, StringComparer.Ordinal).ToList();

        foreach (var point in fresh.OrderBy(p => p.TimeUtc, StringComparer.Ordinal))
        {
            if (point.PointType == TrackPointType.StationaryStart &&
                list.Any(p => p.PointType == TrackPointType.StationaryStart && IsSamePlace(p, point)))
                continue;

            if (point.PointType == TrackPointType.StationaryEnd)
            {
                var existingEnd = list.LastOrDefault(p =>
                    p.PointType == TrackPointType.StationaryEnd && IsSamePlace(p, point));

                if (existingEnd != null)
                {
                    if (string.Compare(point.TimeUtc, existingEnd.TimeUtc, StringComparison.Ordinal) > 0)
                        list.Remove(existingEnd);
                    else
                        continue;
                }
            }

            list.Add(point);
        }

        return list
            .OrderBy(p => p.TimeUtc, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsSamePlace(TrackPointDto left, TrackPointDto right)
    {
        if (left.Lat == null || left.Lon == null || right.Lat == null || right.Lon == null)
            return false;

        return GeoDistance.HaversineMeters(left.Lat.Value, left.Lon.Value, right.Lat.Value, right.Lon.Value) <= 100;
    }

    private static TrackPointDto[] FilterDisplayPoints(IEnumerable<TrackPointDto> points) =>
        points
            .Where(p => p.Lat.HasValue && p.Lon.HasValue)
            .ToArray();

    private TelemetryPoint? FindUnknownGapAnchorTelemetry(string deviceId, DateTime firstUtc)
    {
        var lookbackUtc = firstUtc.AddHours(-Math.Max(1, _trackProcessingSettings.MaxStationarySegmentHours));
        var preceding = _telemetryStore.GetTrack(deviceId, lookbackUtc, firstUtc, limit: 500);
        TelemetryPoint? lastCoordinateBeforeFirst = null;

        for (var index = preceding.Count - 1; index >= 0; index--)
        {
            var point = preceding[index];
            if (point.GpsTimeUtc >= firstUtc)
                continue;

            if (!HasTrackCoordinates(point.Latitude, point.Longitude, point.Accuracy))
                continue;

            lastCoordinateBeforeFirst = point;
            break;
        }

        if (lastCoordinateBeforeFirst == null)
            return null;

        return lastCoordinateBeforeFirst;
    }

    private TrackPointDto[] IncludeUnknownGapAnchor(
        TrackPointDto[] points,
        string deviceId,
        DateTime fromUtc)
    {
        if (points.Length == 0)
            return points;

        if (!TryParseDisplayTimeUtc(points[0].TimeUtc, out var firstUtc) || points[0].Speed <= UnknownGapHelper.MinNextSpeedKmh)
            return points;

        var anchorTelemetry = FindUnknownGapAnchorTelemetry(deviceId, firstUtc);
        if (anchorTelemetry == null)
            return points;

        var gapMinutes = (firstUtc - anchorTelemetry.GpsTimeUtc).TotalMinutes;
        if (gapMinutes <= UnknownGapHelper.MinGapMinutes)
            return points;

        var anchorPoint = MapRawPoint(anchorTelemetry);
        if (!anchorPoint.Lat.HasValue || !anchorPoint.Lon.HasValue)
            return points;

        return [anchorPoint, ..points];
    }

    private static TrackPointDto[] FilterToTimeRange(
        TrackPointDto[] points,
        DateTime fromUtc,
        DateTime toUtc,
        bool keepUnknownGapAnchors = false) =>
        points
            .Where(p => IsPointInTimeRange(p, fromUtc, toUtc, keepUnknownGapAnchors))
            .ToArray();

    private static bool IsPointInTimeRange(
        TrackPointDto point,
        DateTime fromUtc,
        DateTime toUtc,
        bool keepUnknownGapAnchors = false)
    {
        if (!TryParseDisplayTimeUtc(point.TimeUtc, out var pointUtc))
            return false;

        if (pointUtc >= fromUtc && pointUtc <= toUtc)
            return true;

        return keepUnknownGapAnchors &&
            point.PointType == TrackPointType.UnknownGapStart &&
            pointUtc < fromUtc;
    }

    private IReadOnlyList<ProcessedTrackPoint> IncludePrecedingStationaryGroup(
        IReadOnlyList<ProcessedTrackPoint> processedPoints,
        string deviceId,
        DateTime fromUtc)
    {
        if (processedPoints.Count == 0)
            return processedPoints;

        if (processedPoints[0].PointType == Models.TrackPointType.StationaryStart)
            return processedPoints;

        var precedingGroup = _trackPointStore.GetPrecedingStationaryGroup(
            deviceId,
            fromUtc,
            processedPoints[0].TimestampUtc);

        if (precedingGroup.Count == 0)
            return processedPoints;

        var groupEndUtc = precedingGroup
            .Where(p => p.PointType == Models.TrackPointType.StationaryEnd)
            .LastOrDefault()
            ?.TimestampUtc;

        if (!groupEndUtc.HasValue || groupEndUtc.Value < fromUtc)
            return processedPoints;

        return precedingGroup.Concat(processedPoints).ToArray();
    }

    private IReadOnlyList<ProcessedTrackPoint> ProcessFreshPointsForDisplay(
        string normalizedId,
        IReadOnlyList<TelemetryPoint> freshInRange,
        long? lastProcessedRawId,
        DateTime fromUtc,
        DateTime toUtc,
        DeviceProtocol protocol)
    {
        if (freshInRange.Count == 0)
            return Array.Empty<ProcessedTrackPoint>();

        var combined = TrackProcessor.BuildPointsWithContext(
            _telemetryStore,
            normalizedId,
            freshInRange,
            lastProcessedRawId,
            _trackProcessingSettings,
            out _);

        var filtered = TrackSpeedHelper.ApplyDerivedSpeed(
            TrackOutlierFilter.FilterForRuntime(combined, _trackProcessingSettings, protocol));
        if (filtered.Count == 0)
            return Array.Empty<ProcessedTrackPoint>();

        var result = TrackSegmentProcessor.Process(
            normalizedId,
            filtered,
            _trackProcessingSettings,
            "runtime",
            DateTime.UtcNow);

        return result.TrackPoints
            .Where(p => p.TimestampUtc >= fromUtc && p.TimestampUtc <= toUtc)
            .ToArray();
    }

    private static (DateTime FromLocal, DateTime ToLocal) ResolveRange(string? from, string? to)
    {
        var nowLocal = AppTime.NowLocal();

        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
            return (nowLocal.Date, nowLocal);

        var fromLocal = ParseLocalDateTime(from, isEnd: false) ?? nowLocal.Date;
        var toLocal = ParseLocalDateTime(to, isEnd: true) ?? nowLocal;

        if (toLocal < fromLocal)
            toLocal = fromLocal;

        return (fromLocal, toLocal);
    }

    private static DateTime? ParseLocalDateTime(string? value, bool isEnd)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
            return isEnd ? dateOnly.AddDays(1).AddTicks(-1) : dateOnly;

        if (DateTime.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out dateOnly))
            return isEnd ? dateOnly.AddDays(1).AddTicks(-1) : dateOnly;

        if (DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
            return dateTime;

        if (DateTime.TryParseExact(value, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime))
            return dateTime;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime))
            return dateTime;

        throw new ArgumentException($"Неверный формат даты: {value}");
    }

    private TrackPointDto MapProcessedPoint(ProcessedTrackPoint point) =>
        new()
        {
            Lat = point.Latitude,
            Lon = point.Longitude,
            Alt = point.Altitude,
            Speed = point.SpeedKmh,
            Accuracy = point.Accuracy,
            TimeUtc = FormatDisplayTimeUtc(point.TimestampUtc),
            TimeLocal = FormatDisplayTimeLocal(point.TimestampUtc),
            PointType = point.PointType
        };

    private TrackPointDto MapFreshRawPoint(TelemetryPoint point)
    {
        var exposeCoordinates = HasTrackCoordinates(point.Latitude, point.Longitude, point.Accuracy);
        var displayTimeUtc = TrackTimeHelper.ResolveDisplayTimeUtc(point.GpsTimeUtc, point.ReceivedAtUtc);

        return new TrackPointDto
        {
            Lat = exposeCoordinates ? point.Latitude : null,
            Lon = exposeCoordinates ? point.Longitude : null,
            Alt = point.Altitude,
            Speed = point.SpeedKmh,
            Accuracy = point.Accuracy,
            TimeUtc = FormatDisplayTimeUtc(displayTimeUtc),
            TimeLocal = FormatDisplayTimeLocal(displayTimeUtc),
            Geofences = point.Geofences.ToArray(),
            PointType = TrackPointType.RawValid
        };
    }

    private TrackPointDto MapRawPoint(TelemetryPoint point)
    {
        var exposeCoordinates = HasTrackCoordinates(point.Latitude, point.Longitude, point.Accuracy);

        return new TrackPointDto
        {
            Lat = exposeCoordinates ? point.Latitude : null,
            Lon = exposeCoordinates ? point.Longitude : null,
            Alt = point.Altitude,
            Speed = 0,
            Accuracy = point.Accuracy,
            TimeUtc = FormatDisplayTimeUtc(TrackTimeHelper.ResolveDisplayTimeUtc(point.GpsTimeUtc, point.ReceivedAtUtc)),
            TimeLocal = FormatDisplayTimeLocal(TrackTimeHelper.ResolveDisplayTimeUtc(point.GpsTimeUtc, point.ReceivedAtUtc)),
            Geofences = point.Geofences.ToArray()
        };
    }

    private static string FormatDisplayTimeUtc(DateTime timeUtc) =>
        AppTime.AsUtc(timeUtc).ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDisplayTimeLocal(DateTime timeUtc) =>
        AppTime.UtcToLocal(timeUtc).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    private bool HasTrackCoordinates(double? latitude, double? longitude, double? accuracy)
    {
        if (!latitude.HasValue || !longitude.HasValue)
            return false;

        if (!accuracy.HasValue)
            return true;

        return accuracy.Value <= _maxTrackAccuracyMeters;
    }
}
