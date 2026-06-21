using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class ServiceStatusService
{
    private readonly TelemetryStore _telemetryStore;
    private readonly TrackPointStore _trackPointStore;
    private readonly TrackProcessingSettings _trackProcessingSettings;

    public ServiceStatusService(
        TelemetryStore telemetryStore,
        TrackPointStore trackPointStore,
        TrackProcessingSettings trackProcessingSettings)
    {
        _telemetryStore = telemetryStore;
        _trackPointStore = trackPointStore;
        _trackProcessingSettings = trackProcessingSettings;
    }

    public DevicesResponse GetDevicesStatus(string? username = null)
    {
        var trafficSize = LogFiles.Traffic.GetDirectorySizeBytes();
        var rawSize = LogFiles.Raw.GetDirectorySizeBytes();
        var nowLocal = AppTime.NowLocal();
        var todayFromUtc = AppTime.LocalToUtc(nowLocal.Date);
        var todayToUtc = AppTime.LocalToUtc(nowLocal);
        var monthFromUtc = AppTime.LocalToUtc(new DateTime(nowLocal.Year, nowLocal.Month, 1));

        var devices = UserRegistry.FilterDevices(username, DeviceRegistry.GetAll());

        return new DevicesResponse
        {
            Devices = devices
                .Select(device => MapDevice(
                    device,
                    _telemetryStore.GetDeviceStats(device.Id),
                    ResolveOptimizedStats(device.Id, todayFromUtc, todayToUtc, monthFromUtc, todayToUtc)))
                .ToArray(),
            Service = new ServiceStatusDto
            {
                Version = AppVersion.Version,
                UptimeSeconds = ServiceRuntime.UptimeSeconds,
                TrafficLogsSizeBytes = trafficSize,
                RawLogsSizeBytes = rawSize,
                LogsSizeBytes = trafficSize + rawSize,
                DatabaseSizeBytes = _telemetryStore.GetDatabaseSizeBytes()
            }
        };
    }

    private (PeriodTrackStats Today, PeriodTrackStats Month) ResolveOptimizedStats(
        string deviceId,
        DateTime todayFromUtc,
        DateTime todayToUtc,
        DateTime monthFromUtc,
        DateTime monthToUtc)
    {
        if (!_trackProcessingSettings.Enabled)
            return (EmptyPeriodStats(), EmptyPeriodStats());

        return (
            _trackPointStore.GetPeriodStats(deviceId, todayFromUtc, todayToUtc),
            _trackPointStore.GetPeriodStats(deviceId, monthFromUtc, monthToUtc));
    }

    private static DeviceStatusDto MapDevice(
        DeviceDto device,
        DeviceTelemetryStats stats,
        (PeriodTrackStats Today, PeriodTrackStats Month) optimized) =>
        new()
        {
            Id = device.Id,
            Name = device.Name,
            Protocol = device.Protocol,
            PointsCount = stats.PointsCount,
            LastTelemetryAgoSeconds = stats.LastTelemetryAgoSeconds,
            LastGpsAgoSeconds = stats.LastGpsAgoSeconds,
            MonthKm = Math.Round(stats.MonthKmRaw, 2),
            TodayPointsRaw = stats.TodayPointsRaw,
            TodayPointsOptimized = optimized.Today.PointsCount,
            TodayHeartbeatPoints = optimized.Today.HeartbeatPointsCount,
            TodayKmRaw = Math.Round(stats.TodayKmRaw, 2),
            TodayKmOptimized = Math.Round(optimized.Today.DistanceKm, 2),
            MonthPointsRaw = stats.MonthPointsRaw,
            MonthPointsOptimized = optimized.Month.PointsCount,
            MonthHeartbeatPoints = optimized.Month.HeartbeatPointsCount,
            MonthKmRaw = Math.Round(stats.MonthKmRaw, 2),
            MonthKmOptimized = Math.Round(optimized.Month.DistanceKm, 2)
        };

    private static PeriodTrackStats EmptyPeriodStats() =>
        new()
        {
            PointsCount = 0,
            HeartbeatPointsCount = 0,
            DistanceKm = 0
        };
}
