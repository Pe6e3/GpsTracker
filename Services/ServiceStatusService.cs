using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class ServiceStatusService
{
    private readonly TelemetryStore _telemetryStore;

    public ServiceStatusService(TelemetryStore telemetryStore)
    {
        _telemetryStore = telemetryStore;
    }

    public DevicesResponse GetDevicesStatus()
    {
        var trafficSize = LogFiles.Traffic.GetDirectorySizeBytes();
        var rawSize = LogFiles.Raw.GetDirectorySizeBytes();

        return new DevicesResponse
        {
            Devices = DeviceRegistry.GetAll()
                .Select(device => MapDevice(device, _telemetryStore.GetDeviceStats(device.Id)))
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

    private static DeviceStatusDto MapDevice(DeviceDto device, DeviceTelemetryStats stats) =>
        new()
        {
            Id = device.Id,
            Name = device.Name,
            Protocol = device.Protocol,
            PointsCount = stats.PointsCount,
            LastTelemetryAgoSeconds = stats.LastTelemetryAgoSeconds,
            MonthKm = Math.Round(stats.MonthKm, 2)
        };
}
