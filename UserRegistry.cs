using System.Text.Json;

namespace GpsTcpProxy;

public static class UserRegistry
{
    private static readonly Dictionary<string, string> Users = new(StringComparer.OrdinalIgnoreCase);

    public static void Load(string path)
    {
        Users.Clear();

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

            var password = ParsePassword(value);
            if (string.IsNullOrEmpty(password))
                continue;

            Users[username.Trim()] = password;
        }
    }

    public static void LoadFallback(string username, string password)
    {
        if (Users.Count > 0)
            return;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return;

        Users[username.Trim()] = password;
    }

    public static bool TryValidate(string username, string password, out string normalizedUsername)
    {
        normalizedUsername = string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return false;

        if (!Users.TryGetValue(username.Trim(), out var expectedPassword))
            return false;

        if (!string.Equals(expectedPassword, password, StringComparison.Ordinal))
            return false;

        normalizedUsername = Users.Keys.First(key =>
            key.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static string? ParsePassword(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        if (value.TryGetProperty("password", out var passwordElement))
            return passwordElement.GetString();

        if (value.TryGetProperty("Password", out passwordElement))
            return passwordElement.GetString();

        return null;
    }
}
