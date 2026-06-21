namespace GpsTcpProxy.Protocol;

public sealed class HqLocationReport
{
    public required DateTime DeviceTimeUtc { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double SpeedKmh { get; init; }
    public int Direction { get; init; }
    public bool GpsValid { get; init; }
}
