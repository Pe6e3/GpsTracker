using System.Text.Json;

namespace GpsTcpProxy;

public sealed class ProxySettings
{
    public int ListenPort { get; set; } = 8185;
    public int ApiPort { get; set; } = 5081;
    public int UtcOffset { get; set; } = 5;
    public int ServerUtcOffset { get; set; } = 2;
    public int DeviceUtcOffset { get; set; } = 8;
    public string DatabasePath { get; set; } = "data/telemetry.db";
    public string AuthUsername { get; set; } = "pe6e3";
    public string AuthPassword { get; set; } = "12345678";
    public string JwtSecret { get; set; } = "GpsTcpProxy-Change-Me-In-Production-32chars!";
    public string JwtIssuer { get; set; } = "GpsTcpProxy";
    public string JwtAudience { get; set; } = "GpsTcpProxyClient";
    public int JwtExpireHours { get; set; } = 24;

    public static ProxySettings Load(string path)
    {
        if (!File.Exists(path))
            return new ProxySettings();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProxySettings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new ProxySettings();
    }
}
