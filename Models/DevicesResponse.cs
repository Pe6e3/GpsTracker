namespace GpsTcpProxy.Models;

public sealed class DevicesResponse
{
    public required IReadOnlyList<DeviceStatusDto> Devices { get; init; }
    public required ServiceStatusDto Service { get; init; }
}

public sealed class DeviceStatusDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Protocol { get; init; }
    public long PointsCount { get; init; }
    public long? LastTelemetryAgoSeconds { get; init; }
    public long? LastGpsAgoSeconds { get; init; }
    public double MonthKm { get; init; }
    public long TodayPointsRaw { get; init; }
    public long TodayPointsOptimized { get; init; }
    public long TodayHeartbeatPoints { get; init; }
    public double TodayKmRaw { get; init; }
    public double TodayKmOptimized { get; init; }
    public long MonthPointsRaw { get; init; }
    public long MonthPointsOptimized { get; init; }
    public long MonthHeartbeatPoints { get; init; }
    public double MonthKmRaw { get; init; }
    public double MonthKmOptimized { get; init; }
}

public sealed class ServiceStatusDto
{
    public required string Version { get; init; }
    public long UptimeSeconds { get; init; }
    public long TrafficLogsSizeBytes { get; init; }
    public long RawLogsSizeBytes { get; init; }
    public long LogsSizeBytes { get; init; }
    public long DatabaseSizeBytes { get; init; }
}
