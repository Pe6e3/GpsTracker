using System.Text.Json;
using GpsTcpProxy.Models;

namespace GpsTcpProxy;

public static class DeviceRegistry
{
    private static readonly Dictionary<string, DeviceEntry> Devices = new(StringComparer.Ordinal);

    public static void Load(string path)
    {
        Devices.Clear();

        if (!File.Exists(path))
            return;

        var json = File.ReadAllText(path);
        var items = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (items == null)
            return;

        foreach (var (id, value) in items)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var entry = ParseEntry(value);
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                continue;

            Devices[NormalizeId(id)] = entry;
        }
    }

    private static DeviceEntry? ParseEntry(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var name = value.GetString();
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return new DeviceEntry { Name = name.Trim(), Protocol = DeviceProtocol.Jt808 };
        }

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        var entryName = value.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : value.TryGetProperty("Name", out nameElement)
                ? nameElement.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(entryName))
            return null;

        var protocolText = value.TryGetProperty("protocol", out var protocolElement)
            ? protocolElement.GetString()
            : value.TryGetProperty("Protocol", out protocolElement)
                ? protocolElement.GetString()
                : null;

        var protocol = DeviceProtocol.Jt808;
        if (!string.IsNullOrWhiteSpace(protocolText))
            DeviceProtocolParser.TryParse(protocolText, out protocol);

        var proxyHost = value.TryGetProperty("proxyHost", out var proxyHostElement)
            ? proxyHostElement.GetString()
            : value.TryGetProperty("ProxyHost", out proxyHostElement)
                ? proxyHostElement.GetString()
                : null;

        int? proxyPort = null;
        if (value.TryGetProperty("proxyPort", out var proxyPortElement) && proxyPortElement.TryGetInt32(out var port))
            proxyPort = port;
        else if (value.TryGetProperty("ProxyPort", out proxyPortElement) && proxyPortElement.TryGetInt32(out port))
            proxyPort = port;

        return new DeviceEntry
        {
            Name = entryName.Trim(),
            Protocol = protocol,
            ProxyHost = string.IsNullOrWhiteSpace(proxyHost) ? null : proxyHost.Trim(),
            ProxyPort = proxyPort
        };
    }

    public static string NormalizeId(string terminalId)
    {
        if (string.IsNullOrWhiteSpace(terminalId) || terminalId == "-")
            return string.Empty;

        var trimmed = terminalId.Trim();

        if (trimmed.StartsWith('1') && trimmed.Length == 11)
            return trimmed[1..];

        return trimmed;
    }

    public static string GetDisplayName(string? terminalId)
    {
        if (string.IsNullOrWhiteSpace(terminalId) || terminalId == "-")
            return "?";

        var shortId = NormalizeId(terminalId);
        if (Devices.TryGetValue(shortId, out var entry))
            return entry.Name;

        if (Devices.TryGetValue(terminalId, out entry))
            return entry.Name;

        return shortId;
    }

    public static DeviceProtocol GetProtocol(string? terminalId)
    {
        var shortId = NormalizeId(terminalId ?? string.Empty);
        if (!string.IsNullOrEmpty(shortId) && Devices.TryGetValue(shortId, out var entry))
            return entry.Protocol;

        if (!string.IsNullOrWhiteSpace(terminalId) && Devices.TryGetValue(terminalId, out entry))
            return entry.Protocol;

        return DeviceProtocol.Jt808;
    }

    public static DeviceProtocol? GetProtocolOrNull(string? terminalId)
    {
        var shortId = NormalizeId(terminalId ?? string.Empty);
        if (!string.IsNullOrEmpty(shortId) && Devices.TryGetValue(shortId, out var entry))
            return entry.Protocol;

        if (!string.IsNullOrWhiteSpace(terminalId) && Devices.TryGetValue(terminalId, out entry))
            return entry.Protocol;

        return null;
    }

    public static bool Exists(string? terminalId)
    {
        var shortId = NormalizeId(terminalId ?? string.Empty);
        return !string.IsNullOrEmpty(shortId) && Devices.ContainsKey(shortId);
    }

    public static bool ShouldProxyToRemote(string? terminalId)
    {
        var shortId = NormalizeId(terminalId ?? string.Empty);
        if (string.IsNullOrEmpty(shortId))
            return false;

        if (Devices.TryGetValue(shortId, out var entry))
            return entry.Protocol == DeviceProtocol.Gt23;

        return false;
    }

    public static (string Host, int Port) GetProxyEndpoint(string? terminalId)
    {
        const string defaultHost = "27.aika168.com";
        const int defaultPort = 8185;

        var shortId = NormalizeId(terminalId ?? string.Empty);
        if (!string.IsNullOrEmpty(shortId) && Devices.TryGetValue(shortId, out var entry))
        {
            return (
                string.IsNullOrWhiteSpace(entry.ProxyHost) ? defaultHost : entry.ProxyHost,
                entry.ProxyPort is > 0 and <= 65535 ? entry.ProxyPort.Value : defaultPort);
        }

        return (defaultHost, defaultPort);
    }

    public static IReadOnlyList<DeviceDto> GetAll() =>
        Devices
            .OrderBy(x => x.Value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new DeviceDto
            {
                Id = x.Key,
                Name = x.Value.Name,
                Protocol = DeviceProtocolParser.ToConfigValue(x.Value.Protocol)
            })
            .ToArray();
}
