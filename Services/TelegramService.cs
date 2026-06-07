using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class TelegramService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly TelegramSettings _settings;

    public TelegramService(TelegramSettings settings)
    {
        _settings = settings;
    }

    public bool IsConfigured =>
        _settings.Enabled &&
        !string.IsNullOrWhiteSpace(_settings.BotToken) &&
        !string.IsNullOrWhiteSpace(_settings.ChatId);

    public void NotifyLocationChanged(
        string deviceId,
        string? deviceName,
        double latitude,
        double longitude,
        double speedKmh,
        int altitude,
        DateTime gpsTimeUtc,
        IReadOnlyList<string> geofences)
    {
        if (!IsConfigured)
            return;

        var text = FormatLocationMessage(deviceId, deviceName, latitude, longitude, speedKmh, altitude, gpsTimeUtc, geofences);
        _ = SendMessageAsync(text);
    }

    private static string FormatLocationMessage(
        string deviceId,
        string? deviceName,
        double latitude,
        double longitude,
        double speedKmh,
        int altitude,
        DateTime gpsTimeUtc,
        IReadOnlyList<string> geofences)
    {
        var label = FormatDeviceLabel(deviceId, deviceName);
        var gpsTimeLocal = AppTime.UtcToLocal(gpsTimeUtc).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var geofenceText = geofences.Count > 0
            ? string.Join(", ", geofences)
            : "—";

        return
            $"📍 {label}\n" +
            $"{latitude:F5}, {longitude:F5}\n" +
            $"Скорость: {speedKmh:F1} км/ч, высота: {altitude} м\n" +
            $"GPS: {gpsTimeLocal}\n" +
            $"Геозоны: {geofenceText}";
    }

    private static string FormatDeviceLabel(string deviceId, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName == deviceId || deviceName == "?")
            return deviceId;

        return $"{deviceName} ({deviceId})";
    }

    private async Task SendMessageAsync(string text)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_settings.BotToken.Trim()}/sendMessage";
            var payload = new TelegramSendMessageRequest
            {
                ChatId = _settings.ChatId.Trim(),
                Text = text,
                DisableWebPagePreview = true
            };

            using var response = await HttpClient.PostAsJsonAsync(url, payload);
            if (response.IsSuccessStatusCode)
                return;

            var body = await response.Content.ReadAsStringAsync();
            TelegramLogger.LogError($"send failed ({(int)response.StatusCode}): {body}");
        }
        catch (Exception ex)
        {
            TelegramLogger.LogError($"send failed: {ex.Message}");
        }
    }

    private sealed class TelegramSendMessageRequest
    {
        [JsonPropertyName("chat_id")]
        public required string ChatId { get; init; }

        [JsonPropertyName("text")]
        public required string Text { get; init; }

        [JsonPropertyName("disable_web_page_preview")]
        public bool DisableWebPagePreview { get; init; }
    }
}
