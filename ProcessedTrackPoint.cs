namespace GpsTcpProxy;

public sealed class ProcessedTrackPoint
{
    public long Id { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double SpeedKmh { get; init; }
    public int Course { get; init; }
    public int Altitude { get; init; }
    public double? Accuracy { get; init; }
    public long? SourceRawTelemetryId { get; init; }
    public string PointType { get; init; } = string.Empty;
    public string ProcessingBatchId { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public int? OriginalPointsCount { get; init; }
    public long? RawStartId { get; init; }
    public long? RawEndId { get; init; }
}
