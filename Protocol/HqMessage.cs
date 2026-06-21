namespace GpsTcpProxy.Protocol;

public sealed class HqMessage
{
    public required string DeviceId { get; init; }
    public required string PacketType { get; init; }
    public required byte[] RawFrame { get; init; }
    public bool IsBinary { get; init; }
    public HqLocationReport? Location { get; init; }
}
