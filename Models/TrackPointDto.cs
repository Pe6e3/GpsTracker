namespace GpsTcpProxy.Models;

public sealed class TrackPointDto
{
    public double Lat { get; init; }
    public double Lon { get; init; }
    public int Alt { get; init; }
    public double Speed { get; init; }
    public int Direction { get; init; }
    public required string TimeUtc { get; init; }
    public required string TimeLocal { get; init; }
    public string[] Geofences { get; init; } = Array.Empty<string>();
}
