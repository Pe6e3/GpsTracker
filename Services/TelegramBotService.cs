using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed partial class TelegramBotService
{
    private static readonly TimeSpan WakeupResponseTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(35)
    };

    private readonly TelegramSettings _settings;
    private readonly ProxySettings _proxySettings;
    private readonly TelegramService _telegramService;
    private readonly TelegramNotificationGate _notificationGate;
    private readonly IServiceProvider _serviceProvider;
    private long _lastUpdateId;

    public TelegramBotService(
        TelegramSettings settings,
        ProxySettings proxySettings,
        TelegramService telegramService,
        TelegramNotificationGate notificationGate,
        IServiceProvider serviceProvider)
    {
        _settings = settings;
        _proxySettings = proxySettings;
        _telegramService = telegramService;
        _notificationGate = notificationGate;
        _serviceProvider = serviceProvider;
    }

    public bool IsConfigured =>
        _settings.Enabled &&
        !string.IsNullOrWhiteSpace(_settings.BotToken) &&
        !string.IsNullOrWhiteSpace(_settings.ChatId);

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            return;

        _notificationGate.CheckExpiredMute();

        var url =
            $"https://api.telegram.org/bot{_settings.BotToken.Trim()}/getUpdates" +
            $"?offset={_lastUpdateId + 1}&timeout=25&allowed_updates=[\"message\"]";

        using var response = await HttpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            TelegramLogger.LogError($"getUpdates failed ({(int)response.StatusCode}): {body}");
            return;
        }

        var payload = await response.Content.ReadFromJsonAsync<TelegramUpdatesResponse>(cancellationToken);
        if (payload?.Ok != true || payload.Result == null)
            return;

        foreach (var update in payload.Result)
        {
            _lastUpdateId = Math.Max(_lastUpdateId, update.UpdateId);
            await HandleUpdateAsync(update, cancellationToken);
        }
    }

    private async Task HandleUpdateAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        var message = update.Message;
        if (message?.Text == null)
            return;

        if (!IsAllowedChat(message.Chat?.Id))
            return;

        var command = message.Text.Trim();
        if (command.StartsWith('/'))
            command = command[1..];

        var match = StopCommandRegex().Match(command);
        if (match.Success)
        {
            var hoursText = match.Groups[1].Value;
            if (string.IsNullOrEmpty(hoursText))
            {
                _notificationGate.StopPermanent();
                await _telegramService.SendRawAsync("🔕 Уведомления отключены (stop). Отправьте start для включения.", cancellationToken);
                return;
            }

            if (!int.TryParse(hoursText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
            {
                await _telegramService.SendRawAsync("Формат: stop или stop14 (часы).", cancellationToken);
                return;
            }

            _notificationGate.StopForHours(hours);
            var untilLocal = AppTime.UtcToLocal(DateTime.UtcNow.AddHours(hours));
            await _telegramService.SendRawAsync(
                $"🔕 Уведомления отключены на {hours} ч (до {untilLocal:dd.MM.yyyy HH:mm}).",
                cancellationToken);
            return;
        }

        if (string.Equals(command, "start", StringComparison.OrdinalIgnoreCase))
        {
            _notificationGate.Start();
            await _telegramService.SendRawAsync("🔔 Уведомления включены.", cancellationToken);
            await SendDeviceMapLinksAsync(cancellationToken);
            return;
        }

        if (string.Equals(command, "wakeup", StringComparison.OrdinalIgnoreCase))
        {
            await SendWakeupCommandAsync(cancellationToken);
            return;
        }
    }

    private async Task SendWakeupCommandAsync(CancellationToken cancellationToken)
    {
        var phoneIds = _proxySettings.TheftDetection.GetPhoneDeviceIds();
        if (phoneIds.Count == 0)
        {
            await _telegramService.SendRawAsync("PhoneDeviceIds не заданы в конфиге.", cancellationToken);
            return;
        }

        var mqttService = _serviceProvider.GetRequiredService<MqttService>();
        var lines = await Task.WhenAll(phoneIds.Select(phoneId => BuildWakeupLineAsync(mqttService, phoneId)));

        await _telegramService.SendRawAsync(string.Join('\n', lines), "HTML", CancellationToken.None);
    }

    private async Task<string> BuildWakeupLineAsync(MqttService mqttService, string phoneId)
    {
        if (!DeviceRegistry.Exists(phoneId))
            return $"📲 wakeup → {HtmlEncode(phoneId)} — устройство не найдено";

        if (DeviceRegistry.GetProtocol(phoneId) != DeviceProtocol.OwnTracks)
            return $"📲 wakeup → {HtmlEncode(phoneId)} — не OwnTracks";

        var label = DeviceRegistry.GetDisplayName(phoneId);
        var deviceLabel = label == phoneId || label == "?" ? phoneId : $"{label} ({phoneId})";

        OwnTracksLocationMessage? location;
        try
        {
            location = await mqttService.RequestLocationAndWaitAsync(
                phoneId,
                WakeupResponseTimeout,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            return $"📲 wakeup → {HtmlEncode(deviceLabel)} — ошибка: {HtmlEncode(ex.Message)}";
        }

        if (location == null)
        {
            return
                $"📲 wakeup → {HtmlEncode(deviceLabel)}\n" +
                $"Ответ не пришёл за {(int)WakeupResponseTimeout.TotalSeconds} сек.";
        }

        var googleUrl = MapLinkBuilder.BuildGoogleMapsUrl(location.Latitude, location.Longitude);
        return $"📲 wakeup → {HtmlEncode(deviceLabel)} <a href=\"{HtmlEncode(googleUrl)}\">🗺️</a>";
    }

    private async Task SendDeviceMapLinksAsync(CancellationToken cancellationToken)
    {
        var telemetryStore = _serviceProvider.GetRequiredService<TelemetryStore>();
        var lines = new List<string> { "📍 Текущие позиции:" };

        foreach (var device in DeviceRegistry.GetAll())
        {
            var maxAccuracy = _proxySettings.TheftDetection.IsPhoneDevice(device.Id)
                ? _proxySettings.Mqtt.MaxTrackAccuracyMeters
                : (double?)null;

            var point = telemetryStore.GetLatestPosition(device.Id, maxAccuracy);
            var label = string.IsNullOrWhiteSpace(device.Name) ? device.Id : $"{device.Name} ({device.Id})";

            if (point?.Latitude == null || point.Longitude == null)
            {
                lines.Add($"• {HtmlEncode(label)} — нет GPS");
                continue;
            }

            var lat = point.Latitude.Value;
            var lon = point.Longitude.Value;
            var mapUrl = MapLinkBuilder.BuildDeviceUrl(_settings.MapBaseUrl, device.Id);
            var googleUrl = MapLinkBuilder.BuildGoogleMapsUrl(lat, lon);
            var batterySuffix = point.Battery.HasValue ? $" {point.Battery.Value}%" : string.Empty;

            lines.Add(
                $"• {HtmlEncode(label)} " +
                $"<a href=\"{HtmlEncode(mapUrl)}\">🖥️</a>  " +
                $"<a href=\"{HtmlEncode(googleUrl)}\">🗺️</a>{batterySuffix}");
        }

        await _telegramService.SendRawAsync(string.Join('\n', lines), "HTML", cancellationToken);
    }

    private static string HtmlEncode(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private bool IsAllowedChat(long? chatId)
    {
        if (!chatId.HasValue)
            return false;

        if (!long.TryParse(_settings.ChatId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var allowedChatId))
            return false;

        return chatId.Value == allowedChatId;
    }

    [GeneratedRegex("^stop(\\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StopCommandRegex();

    private sealed class TelegramUpdatesResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; init; }

        [JsonPropertyName("result")]
        public List<TelegramUpdate>? Result { get; init; }
    }

    private sealed class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; init; }

        [JsonPropertyName("message")]
        public TelegramMessage? Message { get; init; }
    }

    private sealed class TelegramMessage
    {
        [JsonPropertyName("chat")]
        public TelegramChat? Chat { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    private sealed class TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }
    }
}
