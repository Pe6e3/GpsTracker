namespace GpsTcpProxy.Models;

public sealed class MqttSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ClientId { get; set; } = "GpsTcpProxy";
    public string Topic { get; set; } = "owntracks/#";
    public bool UseTls { get; set; }
    public int TlsPort { get; set; } = 8883;
    public string? CaCertificatePath { get; set; }
    public string OwnTracksUser { get; set; } = string.Empty;
    public int ReconnectDelaySeconds { get; set; } = 5;
    public bool LogPayload { get; set; } = true;
}
