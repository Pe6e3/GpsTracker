namespace GpsTcpProxy.Models;

public sealed class DeviceEntry
{
    public required string Name { get; init; }
    public DeviceProtocol Protocol { get; init; } = DeviceProtocol.Jt808;
}
