using System.Collections.Concurrent;
using System.Globalization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class TheftDetectionService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TelegramService _telegramService;
    private readonly ProxySettings _settings;
    private readonly ConcurrentDictionary<string, int> _suspiciousCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertUtcByDevice = new(StringComparer.Ordinal);

    public TheftDetectionService(
        IServiceProvider serviceProvider,
        TelegramService telegramService,
        ProxySettings settings)
    {
        _serviceProvider = serviceProvider;
        _telegramService = telegramService;
        _settings = settings;
    }

    public void Analyze(
        string deviceId,
        double latitude,
        double longitude,
        double speedKmh,
        DateTime gpsTimeUtc,
        string geofenceNamesJson)
    {
        var cfg = _settings.TheftDetection;
        if (!cfg.Enabled)
            return;

        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            normalizedId = deviceId.Trim();

        if (DeviceRegistry.GetProtocol(normalizedId) == DeviceProtocol.OwnTracks)
            return;

        var geofences = GeofenceStore.DeserializeNames(geofenceNamesJson);
        if (geofences.Count > 0)
        {
            _suspiciousCounts.TryRemove(normalizedId, out _);
            return;
        }

        var phonePoint = GetTelemetryStore().GetLatestPosition(
            cfg.PhoneDeviceId,
            _settings.Mqtt.MaxTrackAccuracyMeters);

        if (phonePoint?.Latitude == null || phonePoint.Longitude == null)
            return;

        var phoneAgeMinutes = Math.Max(0, (gpsTimeUtc - phonePoint.GpsTimeUtc).TotalMinutes);
        if (phoneAgeMinutes > cfg.MaxPhoneAgeMinutes)
            return;

        var distanceKm = GeoDistance.HaversineKm(
            latitude,
            longitude,
            phonePoint.Latitude.Value,
            phonePoint.Longitude.Value);

        var allowedKm = CalculateAllowedDistanceKm(speedKmh, phonePoint.SpeedKmh, phoneAgeMinutes, distanceKm);
        var isCritical = distanceKm >= cfg.CriticalDistanceKm;

        if (!isCritical && distanceKm <= allowedKm)
        {
            _suspiciousCounts.TryRemove(normalizedId, out _);
            return;
        }

        if (!isCritical)
        {
            var count = _suspiciousCounts.AddOrUpdate(normalizedId, 1, (_, current) => current + 1);
            if (count < cfg.SuspiciousCountBeforeAlert)
                return;
        }

        if (_lastAlertUtcByDevice.TryGetValue(normalizedId, out var lastAlertUtc) &&
            (DateTime.UtcNow - lastAlertUtc).TotalMinutes < cfg.AlertCooldownMinutes)
            return;

        _lastAlertUtcByDevice[normalizedId] = DateTime.UtcNow;
        _suspiciousCounts.TryRemove(normalizedId, out _);

        var deviceName = DeviceRegistry.GetDisplayName(normalizedId);
        var phoneName = DeviceRegistry.GetDisplayName(cfg.PhoneDeviceId);
        var gpsTimeLocal = AppTime.UtcToLocal(gpsTimeUtc).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        TrafficLogger.LogInfo(
            $"[THEFT] {FormatLabel(normalizedId, deviceName)}: вне геозоны, " +
            $"расстояние до {FormatLabel(cfg.PhoneDeviceId, phoneName)} = {distanceKm:F2} км " +
            $"(допуск {allowedKm:F2} км, скорость {speedKmh:F0}/{phonePoint.SpeedKmh:F0} км/ч)");

        _telegramService.NotifyTheftAlert(
            normalizedId,
            deviceName,
            cfg.PhoneDeviceId,
            phoneName,
            latitude,
            longitude,
            phonePoint.Latitude.Value,
            phonePoint.Longitude.Value,
            distanceKm,
            speedKmh,
            phonePoint.SpeedKmh,
            gpsTimeLocal);
    }

    private double CalculateAllowedDistanceKm(
        double trackerSpeedKmh,
        double phoneSpeedKmh,
        double phoneAgeMinutes,
        double actualDistanceKm)
    {
        var cfg = _settings.TheftDetection;

        if (trackerSpeedKmh >= cfg.MovingSpeedThresholdKmh &&
            phoneSpeedKmh >= cfg.MovingSpeedThresholdKmh &&
            actualDistanceKm <= cfg.TogetherMovingMaxKm)
            return cfg.TogetherMovingMaxKm;

        var allowed = cfg.BaseDistanceKm;

        if (phoneAgeMinutes > 0)
            allowed += phoneAgeMinutes * cfg.GpsLagTolerancePerMinuteKm;

        if (trackerSpeedKmh < 5 && phoneSpeedKmh < 5)
            allowed = Math.Min(allowed, cfg.StationaryMaxKm);

        if (trackerSpeedKmh >= cfg.MovingSpeedThresholdKmh && phoneSpeedKmh < 5)
            allowed = Math.Min(allowed, cfg.BaseDistanceKm);

        return allowed;
    }

    private TelemetryStore GetTelemetryStore() =>
        _serviceProvider.GetRequiredService<TelemetryStore>();

    private static string FormatLabel(string deviceId, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName == deviceId || deviceName == "?")
            return deviceId;

        return $"{deviceName} ({deviceId})";
    }
}
