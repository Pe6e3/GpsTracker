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

    public TrackResponse? GetTrack(string deviceId, string? from, string? to)
    {
        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return null;

        var deviceName = DeviceRegistry.GetDisplayName(normalizedId);
        var (fromLocal, toLocal) = ResolveRange(from, to);
        var fromUtc = AppTime.LocalToUtc(fromLocal);
        var toUtc = AppTime.LocalToUtc(toLocal);

        if (!_trackProcessingSettings.Enabled)
        {
            var rawTrack = _telemetryStore.GetTrack(normalizedId, fromUtc, toUtc);
            return new TrackResponse
            {
                DeviceId = normalizedId,
                DeviceName = deviceName,
                Points = ApplyCalculatedSpeed(
                    UnifyStationaryCoordinates(
                        FilterDisplayPoints(rawTrack.Select(MapRawPoint))))
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
        var protocol = DeviceRegistry.GetProtocol(normalizedId);
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

        return new TrackResponse
        {
            DeviceId = normalizedId,
            DeviceName = deviceName,
            Points = ApplyCalculatedSpeed(
                UnifyStationaryCoordinates(
                    FilterDisplayPoints(merged)))
        };
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

    private TrackPointDto[] ApplyCalculatedSpeed(TrackPointDto[] points)
    {
        if (points.Length == 0)
            return points;

        var corrected = new TrackPointDto[points.Length];

        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            var previous = index > 0 ? points[index - 1] : null;
            var next = index < points.Length - 1 ? points[index + 1] : null;
            var speed = TrackSpeedHelper.CalculateSpeedKmh(previous, point, next);

            corrected[index] = Math.Abs(speed - point.Speed) < 0.01
                ? point
                : WithSpeed(point, speed);
        }

        return corrected;
    }

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

        var maxLookbackHours = Math.Max(1, _trackProcessingSettings.MaxStationarySegmentHours);
        var minAllowedStartUtc = fromUtc.AddHours(-maxLookbackHours);
        if (precedingGroup[0].TimestampUtc < minAllowedStartUtc)
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

        var filtered = TrackOutlierFilter.FilterForRuntime(combined, _trackProcessingSettings, protocol);
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
            Speed = point.SpeedKmh,
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
