using System.Globalization;
using GpsTcpProxy.Models;

namespace GpsTcpProxy.Services;

public sealed class TrackProcessor
{
    private const int MaxRawPointsPerBatch = 2000;
    private const int MaxBatchesPerDevicePerRun = 10;

    private readonly TelemetryStore _telemetryStore;
    private readonly TrackPointStore _trackPointStore;
    private readonly TrackProcessingSettings _settings;

    public TrackProcessor(
        TelemetryStore telemetryStore,
        TrackPointStore trackPointStore,
        TrackProcessingSettings settings)
    {
        _telemetryStore = telemetryStore;
        _trackPointStore = trackPointStore;
        _settings = settings;
    }

    public void ProcessAllDevices()
    {
        if (!_settings.Enabled)
            return;

        var cutoffUtc = DateTime.UtcNow.AddMinutes(-_settings.ProcessingDelayMinutes);
        var deviceIds = CollectDeviceIds();

        foreach (var deviceId in deviceIds)
        {
            for (var batchIndex = 0; batchIndex < MaxBatchesPerDevicePerRun; batchIndex++)
            {
                var lastProcessedBefore = _trackPointStore.GetLastProcessedRawId(
                    DeviceRegistry.NormalizeId(deviceId) ?? deviceId);
                ProcessDevice(deviceId, cutoffUtc);
                var lastProcessedAfter = _trackPointStore.GetLastProcessedRawId(
                    DeviceRegistry.NormalizeId(deviceId) ?? deviceId);

                if (lastProcessedAfter == lastProcessedBefore)
                    break;
            }
        }
    }

    public void ProcessDevice(string deviceId, DateTime? cutoffUtc = null)
    {
        if (!_settings.Enabled)
            return;

        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            return;

        var processingCutoffUtc = cutoffUtc ?? DateTime.UtcNow.AddMinutes(-_settings.ProcessingDelayMinutes);
        var lastProcessedRawId = _trackPointStore.GetLastProcessedRawId(normalizedId);
        DateTime? fromUtcMin = null;
        if (!lastProcessedRawId.HasValue && _settings.InitialLookbackDays > 0)
        {
            var lookbackUtc = DateTime.UtcNow.AddDays(-_settings.InitialLookbackDays);
            var todayStartUtc = AppTime.LocalToUtc(AppTime.NowLocal().Date);
            fromUtcMin = todayStartUtc > lookbackUtc ? todayStartUtc : lookbackUtc;
        }

        var rawPoints = _telemetryStore.GetRawTelemetryAfter(
            normalizedId,
            lastProcessedRawId,
            processingCutoffUtc,
            MaxRawPointsPerBatch,
            fromUtcMin);

        if (rawPoints.Count == 0)
            return;

        var protocol = DeviceRegistry.GetProtocol(normalizedId);
        var filteredPoints = TrackOutlierFilter.RemoveOutliers(rawPoints, _settings, protocol);
        var removedOutliersCount = rawPoints.Count - filteredPoints.Count;

        if (filteredPoints.Count == 0)
        {
            var checkpoint = CreateProcessingCheckpoint(normalizedId, rawPoints[^1], createdAtUtc: DateTime.UtcNow);
            _trackPointStore.InsertBatch([checkpoint]);

            LogProcessingResult(
                normalizedId,
                rawPoints[0].GpsTimeUtc,
                rawPoints[^1].GpsTimeUtc,
                rawPoints.Count,
                removedOutliersCount,
                0,
                0,
                1);
            return;
        }

        var batchId = Guid.NewGuid().ToString("N");
        var createdAtUtc = DateTime.UtcNow;
        var result = TrackSegmentProcessor.Process(
            normalizedId,
            filteredPoints,
            _settings,
            batchId,
            createdAtUtc);

        if (result.TrackPoints.Count > 0)
        {
            _trackPointStore.InsertBatch(result.TrackPoints);
            _trackPointStore.MergeAdjacentStationary(normalizedId, _settings);
        }

        LogProcessingResult(
            normalizedId,
            rawPoints[0].GpsTimeUtc,
            rawPoints[^1].GpsTimeUtc,
            rawPoints.Count,
            removedOutliersCount,
            result.StationarySegmentsCount,
            result.MovingSegmentsCount,
            result.TrackPoints.Count);
    }

    private IReadOnlyList<string> CollectDeviceIds()
    {
        var fromTelemetry = _telemetryStore.GetDeviceIdsWithTelemetry();
        var fromRegistry = DeviceRegistry.GetAll().Select(d => d.Id);
        return fromTelemetry
            .Concat(fromRegistry)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProcessedTrackPoint CreateProcessingCheckpoint(
        string deviceId,
        TelemetryPoint lastRawPoint,
        DateTime createdAtUtc) =>
        new()
        {
            DeviceId = deviceId,
            TimestampUtc = lastRawPoint.GpsTimeUtc,
            Latitude = lastRawPoint.Latitude,
            Longitude = lastRawPoint.Longitude,
            SpeedKmh = lastRawPoint.SpeedKmh,
            Course = lastRawPoint.Direction,
            Altitude = lastRawPoint.Altitude,
            Accuracy = lastRawPoint.Accuracy,
            SourceRawTelemetryId = lastRawPoint.Id,
            PointType = TrackPointType.Synthetic,
            ProcessingBatchId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = createdAtUtc,
            RawEndId = lastRawPoint.Id
        };

    private static void LogProcessingResult(
        string deviceId,
        DateTime periodStartUtc,
        DateTime periodEndUtc,
        int rawPointsCount,
        int removedOutliersCount,
        int stationarySegmentsCount,
        int movingSegmentsCount,
        int trackPointsCreated)
    {
        var deviceName = DeviceRegistry.GetDisplayName(deviceId);
        var periodStartLocal = AppTime.UtcToLocal(periodStartUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
        var periodEndLocal = AppTime.UtcToLocal(periodEndUtc).ToString("HH:mm", CultureInfo.InvariantCulture);

        TrafficLogger.LogInfo(
            $"[TrackProcessor] {deviceName} {periodStartLocal}-{periodEndLocal} " +
            $"raw={rawPointsCount} outliers={removedOutliersCount} " +
            $"stationary={stationarySegmentsCount} moving={movingSegmentsCount} created={trackPointsCreated}");
    }
}
