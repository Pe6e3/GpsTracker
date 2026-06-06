namespace GpsTcpProxy.Models;

public sealed class TrackResponse
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required IReadOnlyList<TrackPointDto> Points { get; init; }
}
