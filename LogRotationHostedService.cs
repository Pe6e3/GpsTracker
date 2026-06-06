namespace GpsTcpProxy;

public sealed class LogRotationHostedService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            LogFiles.CheckRotation();

            var now = AppTime.NowLocal();
            var nextMidnight = now.Date.AddDays(1);
            var delay = nextMidnight - now;
            if (delay < TimeSpan.FromSeconds(1))
                delay = TimeSpan.FromSeconds(1);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
