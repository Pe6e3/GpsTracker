using GpsTcpProxy.Models;

namespace GpsTcpProxy;

public sealed class ProxyConnection
{
    private string? _deviceId;

    public ProxyConnection(int connectionId, string clientIp)
    {
        ConnectionId = connectionId;
        ClientIp = clientIp;
        ConnectedAt = DateTime.Now;
        LastActivityAt = ConnectedAt;
    }

    public int ConnectionId { get; }
    public string ClientIp { get; }
    public DateTime ConnectedAt { get; }
    public DateTime LastActivityAt { get; private set; }

    public string? DeviceId
    {
        get => _deviceId;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            _deviceId = value;
        }
    }

    public bool IsDeviceIdentified => !string.IsNullOrEmpty(_deviceId);

    public void Touch() => LastActivityAt = DateTime.Now;

    public string DeviceLabel => _deviceId ?? "-";

    public DeviceProtocol? Protocol { get; set; }
}
