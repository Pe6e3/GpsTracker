using System.Text.Json;

namespace GpsTcpProxy;

public sealed class ProxySettings
{
    public int ListenPort { get; set; } = 8185;
    public string RemoteHost { get; set; } = "27.aika168.com";
    public int RemotePort { get; set; } = 8185;
    public int UtcOffset { get; set; } = 5;
    public int ServerUtcOffset { get; set; } = 2;
    public int DeviceUtcOffset { get; set; } = 8;

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
