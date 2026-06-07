namespace GpsTcpProxy;

public sealed class PeriodTrackStats
{
    public long PointsCount { get; init; }
    public long HeartbeatPointsCount { get; init; }
    public double DistanceKm { get; init; }
}
