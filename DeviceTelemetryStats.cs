namespace GpsTcpProxy;

public sealed class DeviceTelemetryStats
{
    public long PointsCount { get; init; }
    public long? LastTelemetryAgoSeconds { get; init; }
    public long? LastGpsAgoSeconds { get; init; }
    public double MonthKm { get; init; }
    public long TodayPointsRaw { get; init; }
    public long TodayPointsOptimized { get; init; }
    public double TodayKmRaw { get; init; }
    public double TodayKmOptimized { get; init; }
    public long MonthPointsRaw { get; init; }
    public long MonthPointsOptimized { get; init; }
    public double MonthKmRaw { get; init; }
    public double MonthKmOptimized { get; init; }
}
