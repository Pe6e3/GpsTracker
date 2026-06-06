namespace GpsTcpProxy;

public sealed class DeviceTelemetryStats
{
    public long PointsCount { get; init; }
    public long? LastTelemetryAgoSeconds { get; init; }
    public double MonthKm { get; init; }
}
