using System.Globalization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class TrackQueryService
{
    private readonly TelemetryStore _telemetryStore;

    public TrackQueryService(TelemetryStore telemetryStore)
    {
        _telemetryStore = telemetryStore;
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

        var points = _telemetryStore.GetTrack(normalizedId, fromUtc, toUtc);

        return new TrackResponse
        {
            DeviceId = normalizedId,
            DeviceName = deviceName,
            Points = points.Select(MapPoint).ToArray()
        };
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

    private static TrackPointDto MapPoint(TelemetryPoint point) =>
        new()
        {
            Lat = point.Latitude,
            Lon = point.Longitude,
            Alt = point.Altitude,
            Speed = point.SpeedKmh,
            Direction = point.Direction,
            TimeUtc = AppTime.AsUtc(point.GpsTimeUtc).ToString("O", CultureInfo.InvariantCulture),
            TimeLocal = AppTime.UtcToLocal(point.GpsTimeUtc).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
        };
}
