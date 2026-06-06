namespace GpsTcpProxy;

public sealed class TelemetryPoint
{
    public long Id { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string? DeviceName { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int Altitude { get; init; }
    public double SpeedKmh { get; init; }
    public int Direction { get; init; }
    public DateTime GpsTimeUtc { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
}
