namespace GpsTcpProxy.Models;

public sealed class TrackPointDto
{
    public double Lat { get; init; }
    public double Lon { get; init; }
    public int Alt { get; init; }
    public double SpeedKmh { get; init; }
    public int Direction { get; init; }
    public required string TimeUtc { get; init; }
    public required string TimeLocal { get; init; }
}
