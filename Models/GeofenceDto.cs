namespace GpsTcpProxy.Models;

public sealed class GeofenceDto
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<GeofencePointDto> Points { get; init; }
    public required string CreatedAtUtc { get; init; }
    public required string UpdatedAtUtc { get; init; }
}

public sealed class GeofencePointDto
{
    public double Lat { get; init; }
    public double Lon { get; init; }
}

public sealed class GeofenceCreateRequest
{
    public required string Name { get; init; }
    public required IReadOnlyList<GeofencePointDto> Points { get; init; }
}

public sealed class GeofenceUpdateRequest
{
    public required string Name { get; init; }
    public required IReadOnlyList<GeofencePointDto> Points { get; init; }
}

public sealed class GeofencesResponse
{
    public required IReadOnlyList<GeofenceDto> Geofences { get; init; }
}
