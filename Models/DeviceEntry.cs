namespace GpsTcpProxy.Models;

public sealed class DeviceEntry
{
    public required string Name { get; init; }
    public DeviceProtocol Protocol { get; init; } = DeviceProtocol.Jt808;
    public string? ProxyHost { get; init; }
    public int? ProxyPort { get; init; }
}
