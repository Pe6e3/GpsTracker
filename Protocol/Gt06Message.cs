namespace GpsTcpProxy.Protocol;

public sealed class Gt06LocationReport
{
    public required DateTime DeviceTimeUtc { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int SpeedKmh { get; init; }
    public int Direction { get; init; }
    public int SatelliteCount { get; init; }
}

public sealed class Gt06Message
{
    public required byte ProtocolNumber { get; init; }
    public required byte ContentLength { get; init; }
    public required byte[] Payload { get; init; }
    public required ushort Serial { get; init; }
    public required byte[] RawFrame { get; init; }
    public bool ChecksumValid { get; init; }
    public string? Imei { get; init; }
}
