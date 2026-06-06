using System.Text.Json;
using GpsTcpProxy.Models;

namespace GpsTcpProxy;

public static class DeviceRegistry
{
    private static readonly Dictionary<string, string> Devices = new(StringComparer.Ordinal);

    public static void Load(string path)
    {
        Devices.Clear();

        if (!File.Exists(path))
            return;

        var json = File.ReadAllText(path);
        var items = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (items == null)
            return;

        foreach (var (id, name) in items)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                continue;

            Devices[NormalizeId(id)] = name.Trim();
        }
    }

    public static string NormalizeId(string terminalId)
    {
        if (string.IsNullOrWhiteSpace(terminalId) || terminalId == "-")
            return string.Empty;

        if (terminalId.StartsWith('1') && terminalId.Length == 11)
            return terminalId[1..];

        return terminalId.Trim();
    }

    public static string GetDisplayName(string? terminalId)
    {
        if (string.IsNullOrWhiteSpace(terminalId) || terminalId == "-")
            return "?";

        var shortId = NormalizeId(terminalId);
        if (Devices.TryGetValue(shortId, out var name))
            return name;

        if (Devices.TryGetValue(terminalId, out name))
            return name;

        return shortId;
    }

    public static bool Exists(string? terminalId)
    {
        var shortId = NormalizeId(terminalId ?? string.Empty);
        return !string.IsNullOrEmpty(shortId) && Devices.ContainsKey(shortId);
    }

    public static IReadOnlyList<DeviceDto> GetAll() =>
        Devices
            .OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Select(x => new DeviceDto { Id = x.Key, Name = x.Value })
            .ToArray();
}
