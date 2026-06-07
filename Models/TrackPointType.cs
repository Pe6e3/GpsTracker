namespace GpsTcpProxy.Models;

public static class TrackPointType
{
    public const string RawValid = "raw_valid";
    public const string Moving = "moving";
    public const string StationaryStart = "stationary_start";
    public const string StationaryEnd = "stationary_end";
    public const string Heartbeat = "heartbeat";
    public const string Synthetic = "synthetic";
}
