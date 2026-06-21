using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class TelegramService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(35)
    };

    private readonly TelegramSettings _settings;
    private readonly TelegramNotificationGate _notificationGate;

    public TelegramService(TelegramSettings settings, TelegramNotificationGate notificationGate)
    {
        _settings = settings;
        _notificationGate = notificationGate;
    }

    public bool IsConfigured =>
        _settings.Enabled &&
        !string.IsNullOrWhiteSpace(_settings.BotToken);

    public void NotifyGeofenceTransition(
        string deviceId,
        string? deviceName,
        string geofenceName,
        bool entered,
        string? chatId = null)
    {
        if (!IsConfigured || !_notificationGate.CanNotify)
            return;

        var targetChatId = ResolveChatId(chatId);
        if (targetChatId == null)
            return;

        var label = FormatDeviceLabel(deviceId, deviceName);
        var action = entered ? "вошёл в" : "вышел из";
        var icon = entered ? "⬡✅" : "⬡🚪";
        var text = $"{icon} {label}\n{action} «{geofenceName}»";
        _ = SendRawAsync(text, chatId: targetChatId);
    }

    public void NotifyTheftAlert(
        string deviceId,
        string? deviceName,
        string phoneDeviceId,
        string? phoneDeviceName,
        double deviceLat,
        double deviceLon,
        double phoneLat,
        double phoneLon,
        double distanceKm,
        double deviceSpeedKmh,
        double phoneSpeedKmh,
        string gpsTimeLocal,
        string? chatId = null)
    {
        if (!IsConfigured || !_notificationGate.CanNotify)
            return;

        var targetChatId = ResolveChatId(chatId);
        if (targetChatId == null)
            return;

        var deviceLabel = FormatDeviceLabel(deviceId, deviceName);
        var phoneLabel = FormatDeviceLabel(phoneDeviceId, phoneDeviceName);
        var mapUrl = MapLinkBuilder.BuildDeviceUrl(_settings.MapBaseUrl, deviceId);

        var text =
            $"🚨 ВОЗМОЖНЫЙ УГОН\n" +
            $"{deviceLabel}\n" +
            $"{deviceLat:F5}, {deviceLon:F5}\n" +
            $"Скорость: {deviceSpeedKmh:F0} км/ч\n" +
            $"GPS: {gpsTimeLocal}\n" +
            $"Расстояние до {phoneLabel}: {distanceKm:F2} км\n" +
            $"(телефон {phoneSpeedKmh:F0} км/ч)\n" +
            $"Карта: {mapUrl}";

        _ = SendRawAsync(text, chatId: targetChatId);
    }

    public Task SendRawAsync(
        string text,
        string? parseMode = null,
        string? chatId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return Task.CompletedTask;

        var targetChatId = ResolveChatId(chatId);
        if (targetChatId == null)
            return Task.CompletedTask;

        return SendMessageAsync(text, parseMode, targetChatId, cancellationToken);
    }

    private string? ResolveChatId(string? chatId)
    {
        if (!string.IsNullOrWhiteSpace(chatId))
            return chatId.Trim();

        if (!string.IsNullOrWhiteSpace(_settings.ChatId))
            return _settings.ChatId.Trim();

        return null;
    }

    private static string FormatDeviceLabel(string deviceId, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName == deviceId || deviceName == "?")
            return deviceId;

        return $"{deviceName} ({deviceId})";
    }

    private async Task SendMessageAsync(
        string text,
        string? parseMode,
        string chatId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.telegram.org/bot{_settings.BotToken.Trim()}/sendMessage";
            var payload = new TelegramSendMessageRequest
            {
                ChatId = chatId,
                Text = text,
                ParseMode = parseMode,
                DisableWebPagePreview = true
            };

            using var response = await HttpClient.PostAsJsonAsync(url, payload, cancellationToken);
            if (response.IsSuccessStatusCode)
                return;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            TelegramLogger.LogError($"send failed ({(int)response.StatusCode}): {body}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

        [JsonPropertyName("parse_mode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ParseMode { get; init; }

        [JsonPropertyName("disable_web_page_preview")]
        public bool DisableWebPagePreview { get; init; }
    }
}
