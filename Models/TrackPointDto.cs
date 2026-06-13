namespace GpsTcpProxy.Models;

public sealed class TrackPointDto
{
    public double? Lat { get; init; }
    public double? Lon { get; init; }
    public double? Accuracy { get; init; }
    public int Alt { get; init; }
    public double Speed { get; init; }
    public required string TimeUtc { get; init; }
    public required string TimeLocal { get; init; }
    public string[] Geofences { get; init; } = Array.Empty<string>();
    public string? PointType { get; init; }
    public long? SourceRawTelemetryId { get; init; }
    public long? RawStartId { get; init; }
    public long? RawEndId { get; init; }
}
