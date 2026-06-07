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
    public double MonthKm { get; init; }
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
