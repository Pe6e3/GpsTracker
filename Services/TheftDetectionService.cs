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

        if (!UserRegistry.TryGetDeviceOwner(normalizedId, out var ownerUsername))
            return;

        var ownerChatId = UserRegistry.GetTelegramChatId(ownerUsername);
        if (ownerChatId == null)
            return;

        var ownerPhoneIds = UserRegistry.GetPhoneDeviceIdsForOwner(ownerUsername);
        if (ownerPhoneIds.Count == 0)
            return;

        var geofences = GeofenceStore.DeserializeNames(geofenceNamesJson);
        if (geofences.Count > 0)
        {
            _suspiciousCounts.TryRemove(normalizedId, out _);
            return;
        }

        var store = GetTelemetryStore();
        var recentMovementMeters = store.GetMovementDistanceMeters(
            normalizedId,
            gpsTimeUtc,
            cfg.RecentMovementWindowMinutes);

        if (IsCoordinatedDeparture(store, normalizedId, gpsTimeUtc, recentMovementMeters, ownerPhoneIds, cfg))
        {
            _suspiciousCounts.TryRemove(normalizedId, out _);
            return;
        }

        var phoneReference = FindNearestPhoneReference(
            store,
            latitude,
            longitude,
            gpsTimeUtc,
            ownerPhoneIds,
            cfg,
            _settings.Mqtt.MaxTrackAccuracyMeters);
        if (phoneReference == null)
            return;

        var phonePoint = phoneReference.Point;
        var phoneId = phoneReference.DeviceId;
        var phoneAgeMinutes = phoneReference.AgeMinutes;
        var distanceKm = phoneReference.DistanceKm;
        var trackerIsMoving = IsTrackerMoving(speedKmh, recentMovementMeters, cfg);

        var allowedKm = CalculateAllowedDistanceKm(
            speedKmh,
            phonePoint.SpeedKmh,
            phoneAgeMinutes,
            distanceKm,
            recentMovementMeters,
            trackerIsMoving,
            cfg);

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
        var phoneName = DeviceRegistry.GetDisplayName(phoneId);
        var gpsTimeLocal = AppTime.UtcToLocal(gpsTimeUtc).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        TrafficLogger.LogInfo(
            $"[THEFT] {FormatLabel(normalizedId, deviceName)}: вне геозоны, " +
            $"расстояние до {FormatLabel(phoneId, phoneName)} = {distanceKm:F2} км " +
            $"(допуск {allowedKm:F2} км, скорость {speedKmh:F0}/{phonePoint.SpeedKmh:F0} км/ч, " +
            $"отставание телефона {phoneAgeMinutes:F0} мин, смещение {recentMovementMeters:F0} м)");

        _telegramService.NotifyTheftAlert(
            normalizedId,
            deviceName,
            phoneId,
            phoneName,
            latitude,
            longitude,
            phonePoint.Latitude!.Value,
            phonePoint.Longitude!.Value,
            distanceKm,
            speedKmh,
            phonePoint.SpeedKmh,
            gpsTimeLocal,
            ownerChatId);
    }

    private static bool IsCoordinatedDeparture(
        TelemetryStore store,
        string trackerId,
        DateTime gpsTimeUtc,
        double recentMovementMeters,
        IReadOnlyList<string> phoneDeviceIds,
        TheftDetectionSettings cfg)
    {
        if (recentMovementMeters < cfg.RecentMovementDistanceMeters)
            return false;

        var lastTrackerGeofence = store.GetLastGeofencePointBefore(trackerId, gpsTimeUtc);
        if (lastTrackerGeofence == null)
            return false;

        var minutesSinceExit = (gpsTimeUtc - lastTrackerGeofence.GpsTimeUtc).TotalMinutes;
        if (minutesSinceExit > cfg.DepartureGraceMinutes)
            return false;

        foreach (var phoneId in phoneDeviceIds)
        {
            var phonePoint = store.GetLatestPositionAtOrBefore(phoneId, gpsTimeUtc);
            if (phonePoint?.Latitude == null || phonePoint.Longitude == null)
                continue;

            if (SharesGeofence(lastTrackerGeofence.Geofences, phonePoint.Geofences))
                return true;

            var lastPhoneGeofence = store.GetLastGeofencePointBefore(phoneId, gpsTimeUtc);
            if (lastPhoneGeofence != null &&
                SharesGeofence(lastTrackerGeofence.Geofences, lastPhoneGeofence.Geofences))
                return true;
        }

        return false;
    }

    private static bool SharesGeofence(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return false;

        var rightSet = new HashSet<string>(right, StringComparer.OrdinalIgnoreCase);
        return left.Any(name => rightSet.Contains(name));
    }

    private static bool IsTrackerMoving(
        double speedKmh,
        double recentMovementMeters,
        TheftDetectionSettings cfg) =>
        speedKmh >= cfg.MovingSpeedThresholdKmh ||
        recentMovementMeters >= cfg.RecentMovementDistanceMeters;

    private static PhoneReference? FindNearestPhoneReference(
        TelemetryStore store,
        double latitude,
        double longitude,
        DateTime gpsTimeUtc,
        IReadOnlyList<string> phoneDeviceIds,
        TheftDetectionSettings cfg,
        double maxTrackAccuracyMeters)
    {
        PhoneReference? nearest = null;

        foreach (var phoneId in phoneDeviceIds)
        {
            var phonePoint = store.GetLatestPositionAtOrBefore(
                phoneId,
                gpsTimeUtc,
                maxTrackAccuracyMeters);

            if (phonePoint?.Latitude == null || phonePoint.Longitude == null)
                continue;

            var phoneAgeMinutes = Math.Max(0, (gpsTimeUtc - phonePoint.GpsTimeUtc).TotalMinutes);
            if (phoneAgeMinutes > cfg.MaxPhoneAgeMinutes)
                continue;

            var distanceKm = GeoDistance.HaversineKm(
                latitude,
                longitude,
                phonePoint.Latitude.Value,
                phonePoint.Longitude.Value);

            if (nearest == null || distanceKm < nearest.DistanceKm)
            {
                nearest = new PhoneReference(
                    phoneId,
                    phonePoint,
                    distanceKm,
                    phoneAgeMinutes);
            }
        }

        return nearest;
    }

    private sealed record PhoneReference(
        string DeviceId,
        TelemetryPoint Point,
        double DistanceKm,
        double AgeMinutes);

    private static double CalculateAllowedDistanceKm(
        double trackerSpeedKmh,
        double phoneSpeedKmh,
        double phoneAgeMinutes,
        double actualDistanceKm,
        double recentMovementMeters,
        bool trackerIsMoving,
        TheftDetectionSettings cfg)
    {
        if (trackerSpeedKmh >= cfg.MovingSpeedThresholdKmh &&
            phoneSpeedKmh >= cfg.MovingSpeedThresholdKmh &&
            actualDistanceKm <= cfg.TogetherMovingMaxKm)
            return cfg.TogetherMovingMaxKm;

        var allowed = cfg.BaseDistanceKm;

        if (phoneAgeMinutes > 0)
            allowed += phoneAgeMinutes * cfg.GpsLagTolerancePerMinuteKm;

        if (trackerIsMoving)
        {
            allowed += recentMovementMeters / 1000d * 0.35;
            if (phoneAgeMinutes > 0)
                allowed += phoneAgeMinutes * cfg.MovingLagTolerancePerMinuteKm;

            allowed = Math.Min(Math.Max(allowed, cfg.TogetherMovingMaxKm), cfg.MovingLagMaxKm);
        }
        else if (trackerSpeedKmh < 5 &&
                 phoneSpeedKmh < 5 &&
                 phoneAgeMinutes <= cfg.MinPhoneFreshnessForStationaryCapMinutes)
            allowed = Math.Min(allowed, cfg.StationaryMaxKm);

        if (trackerSpeedKmh >= cfg.MovingSpeedThresholdKmh &&
            phoneSpeedKmh < 5 &&
            !trackerIsMoving)
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
