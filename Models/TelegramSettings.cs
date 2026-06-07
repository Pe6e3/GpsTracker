namespace GpsTcpProxy.Models;

public sealed class TelegramSettings
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public string MapBaseUrl { get; set; } = "https://pe6e3.top/map";
}
