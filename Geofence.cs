namespace GpsTcpProxy;

public sealed class Geofence
{
    public long Id { get; init; }
    public required string OwnerUser { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<GeofencePoint> Points { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}
