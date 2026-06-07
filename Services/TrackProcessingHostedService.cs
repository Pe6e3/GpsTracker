using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class TrackProcessingHostedService : BackgroundService
{
    private readonly TrackProcessor _trackProcessor;
    private readonly TrackProcessingSettings _settings;

    public TrackProcessingHostedService(TrackProcessor trackProcessor, TrackProcessingSettings settings)
    {
        _trackProcessor = trackProcessor;
        _settings = settings;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            TrafficLogger.LogInfo("Track processing: disabled");
            return;
        }

        TrafficLogger.LogInfo(
            $"Track processing: enabled (every {_settings.RunEverySeconds}s, delay {_settings.ProcessingDelayMinutes}m)");

        var delay = TimeSpan.FromSeconds(Math.Max(1, _settings.RunEverySeconds));

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Run(_trackProcessor.ProcessAllDevices, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                TrafficLogger.LogInfo($"[TrackProcessor] Ошибка фоновой обработки: {ex.Message}");
            }

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
