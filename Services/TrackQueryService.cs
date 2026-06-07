using System.Globalization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class TrackQueryService
{
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
                Points = rawTrack.Select(MapRawPoint).ToArray()
            };
        }

        var processedPoints = IncludePrecedingStationaryGroup(
            _trackPointStore.GetTrack(normalizedId, fromUtc, toUtc),
            normalizedId,
            fromUtc);
        var lastProcessedRawId = _trackPointStore.GetLastProcessedRawId(normalizedId);
        var freshRawPoints = _telemetryStore.GetRawTelemetryAfter(normalizedId, lastProcessedRawId, toUtc);
        var protocol = DeviceRegistry.GetProtocol(normalizedId);
        var filteredFreshRaw = TrackOutlierFilter.FilterForRuntime(
            freshRawPoints,
            _trackProcessingSettings,
            protocol);

        var freshInRange = filteredFreshRaw
            .Where(p => p.GpsTimeUtc >= fromUtc && p.GpsTimeUtc <= toUtc)
            .ToArray();

        var merged = processedPoints
            .Select(MapProcessedPoint)
            .Concat(freshInRange.Select(MapFreshRawPoint))
            .OrderBy(p => p.TimeUtc, StringComparer.Ordinal)
            .ToArray();

        return new TrackResponse
        {
            DeviceId = normalizedId,
            DeviceName = deviceName,
            Points = merged
        };
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

        return precedingGroup.Concat(processedPoints).ToArray();
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

    private TrackPointDto MapProcessedPoint(ProcessedTrackPoint point)
    {
        var exposeCoordinates = HasTrackCoordinates(point.Latitude, point.Longitude, point.Accuracy);

        return new TrackPointDto
        {
            Lat = exposeCoordinates ? point.Latitude : null,
            Lon = exposeCoordinates ? point.Longitude : null,
            Alt = point.Altitude,
            Speed = point.SpeedKmh,
            Direction = point.Course,
            Accuracy = point.Accuracy,
            TimeUtc = FormatDisplayTimeUtc(point.TimestampUtc),
            TimeLocal = FormatDisplayTimeLocal(point.TimestampUtc),
            PointType = point.PointType
        };
    }

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
            Direction = point.Direction,
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
            Direction = point.Direction,
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
