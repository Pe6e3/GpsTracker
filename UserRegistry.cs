using System.Text.Json;
using GpsTcpProxy.Models;

namespace GpsTcpProxy;

public static class UserRegistry
{
    private static readonly Dictionary<string, UserEntry> Users = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> DeviceOwners = new(StringComparer.Ordinal);

    public static void Load(string path)
    {
        Users.Clear();
        DeviceOwners.Clear();

        if (!File.Exists(path))
            return;

        var json = File.ReadAllText(path);
        var items = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (items == null)
            return;

        foreach (var (username, value) in items)
        {
            if (string.IsNullOrWhiteSpace(username))
                continue;

            var entry = ParseEntry(value);
            if (entry == null)
                continue;

            Users[username.Trim()] = entry;
        }

        RebuildDeviceOwners();
    }

    public static void LoadFallback(string username, string password)
    {
        if (Users.Count > 0)
            return;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return;

        Users[username.Trim()] = new UserEntry
        {
            Password = password,
            DeviceIds = null,
            TelegramChatId = null
        };
    }

    public static bool TryValidate(string username, string password, out string normalizedUsername)
    {
        normalizedUsername = string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return false;

        if (!Users.TryGetValue(username.Trim(), out var entry))
            return false;

        if (!string.Equals(entry.Password, password, StringComparison.Ordinal))
            return false;

        normalizedUsername = Users.Keys.First(key =>
            key.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public static bool CanAccessDevice(string? username, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(deviceId))
            return false;

        if (!Users.TryGetValue(username.Trim(), out var entry))
            return false;

        if (entry.DeviceIds == null || entry.DeviceIds.Count == 0)
            return true;

        var normalizedDeviceId = DeviceRegistry.NormalizeId(deviceId);
        return entry.DeviceIds.Any(id => DeviceRegistry.NormalizeId(id) == normalizedDeviceId);
    }

    public static IReadOnlyList<DeviceDto> FilterDevices(string? username, IReadOnlyList<DeviceDto> devices)
    {
        if (string.IsNullOrWhiteSpace(username) || !Users.TryGetValue(username.Trim(), out var entry))
            return devices;

        if (entry.DeviceIds == null || entry.DeviceIds.Count == 0)
            return devices;

        var allowed = new HashSet<string>(
            entry.DeviceIds.Select(DeviceRegistry.NormalizeId),
            StringComparer.Ordinal);

        return devices
            .Where(device => allowed.Contains(DeviceRegistry.NormalizeId(device.Id)))
            .ToArray();
    }

    public static bool TryGetDeviceOwner(string deviceId, out string username)
    {
        username = string.Empty;

        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            normalizedId = deviceId.Trim();

        if (DeviceOwners.TryGetValue(normalizedId, out var owner))
        {
            username = owner;
            return true;
        }

        var unrestrictedUsers = Users
            .Where(pair => pair.Value.DeviceIds == null || pair.Value.DeviceIds.Count == 0)
            .Select(pair => pair.Key)
            .ToArray();

        if (unrestrictedUsers.Length != 1)
            return false;

        username = unrestrictedUsers[0];
        return true;
    }

    public static string? GetTelegramChatId(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || !Users.TryGetValue(username.Trim(), out var entry))
            return null;

        return string.IsNullOrWhiteSpace(entry.TelegramChatId) ? null : entry.TelegramChatId.Trim();
    }

    public static string? GetTelegramChatIdForDevice(string deviceId) =>
        TryGetDeviceOwner(deviceId, out var username) ? GetTelegramChatId(username) : null;

    public static bool TryGetUsernameByTelegramChatId(string chatId, out string username)
    {
        username = string.Empty;

        if (string.IsNullOrWhiteSpace(chatId))
            return false;

        var normalizedChatId = chatId.Trim();
        foreach (var (user, entry) in Users)
        {
            if (string.IsNullOrWhiteSpace(entry.TelegramChatId))
                continue;

            if (entry.TelegramChatId.Trim() == normalizedChatId)
            {
                username = user;
                return true;
            }
        }

        return false;
    }

    public static bool IsAllowedTelegramChat(string chatId)
    {
        if (string.IsNullOrWhiteSpace(chatId))
            return false;

        return TryGetUsernameByTelegramChatId(chatId, out _);
    }

    public static IReadOnlyList<string> GetPhoneDeviceIdsForOwner(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || !Users.TryGetValue(username.Trim(), out var entry))
            return Array.Empty<string>();

        IEnumerable<string> candidateIds;
        if (entry.DeviceIds == null || entry.DeviceIds.Count == 0)
        {
            candidateIds = DeviceRegistry.GetAll()
                .Where(device => TryGetDeviceOwner(device.Id, out var owner)
                    && owner.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(device => device.Id);
        }
        else
            candidateIds = entry.DeviceIds;

        return candidateIds
            .Where(id => DeviceRegistry.GetProtocol(id) == DeviceProtocol.OwnTracks)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> GetPhoneDeviceIdsForDevice(string deviceId) =>
        TryGetDeviceOwner(deviceId, out var username)
            ? GetPhoneDeviceIdsForOwner(username)
            : Array.Empty<string>();

    private static void RebuildDeviceOwners()
    {
        foreach (var (username, entry) in Users)
        {
            if (entry.DeviceIds == null || entry.DeviceIds.Count == 0)
                continue;

            foreach (var deviceId in entry.DeviceIds)
            {
                var normalizedId = DeviceRegistry.NormalizeId(deviceId);
                if (string.IsNullOrEmpty(normalizedId))
                    normalizedId = deviceId.Trim();

                DeviceOwners[normalizedId] = username;
            }
        }
    }

    private static UserEntry? ParseEntry(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var password = value.GetString();
            if (string.IsNullOrEmpty(password))
                return null;

            return new UserEntry
            {
                Password = password,
                DeviceIds = null,
                TelegramChatId = null
            };
        }

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        var passwordText = value.TryGetProperty("password", out var passwordElement)
            ? passwordElement.GetString()
            : value.TryGetProperty("Password", out passwordElement)
                ? passwordElement.GetString()
                : null;

        if (string.IsNullOrEmpty(passwordText))
            return null;

        return new UserEntry
        {
            Password = passwordText,
            DeviceIds = ParseDeviceIds(value),
            TelegramChatId = ParseTelegramChatId(value)
        };
    }

    private static string? ParseTelegramChatId(JsonElement value)
    {
        if (value.TryGetProperty("telegramChatId", out var chatIdElement)
            || value.TryGetProperty("TelegramChatId", out chatIdElement)
            || value.TryGetProperty("chatId", out chatIdElement)
            || value.TryGetProperty("ChatId", out chatIdElement))
        {
            var chatId = chatIdElement.ValueKind == JsonValueKind.Number
                ? chatIdElement.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : chatIdElement.GetString();

            return string.IsNullOrWhiteSpace(chatId) ? null : chatId.Trim();
        }

        return null;
    }

    private static IReadOnlyList<string>? ParseDeviceIds(JsonElement value)
    {
        if (!value.TryGetProperty("devices", out var devicesElement)
            && !value.TryGetProperty("Devices", out devicesElement))
            return null;

        if (devicesElement.ValueKind != JsonValueKind.Array)
            return null;

        var deviceIds = devicesElement
            .EnumerateArray()
            .Select(element => element.GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return deviceIds.Length == 0 ? null : deviceIds;
    }

    private sealed class UserEntry
    {
        public required string Password { get; init; }
        public IReadOnlyList<string>? DeviceIds { get; init; }
        public string? TelegramChatId { get; init; }
    }
}
