namespace GpsTcpProxy.Services;

public sealed class TelegramBotHostedService : BackgroundService
{
    private readonly TelegramBotService _botService;

    public TelegramBotHostedService(TelegramBotService botService)
    {
        _botService = botService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_botService.IsConfigured)
            return;

        TrafficLogger.LogInfo("Telegram bot: polling commands (stop/start/wakeup)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _botService.PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                TelegramLogger.LogError($"polling failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
