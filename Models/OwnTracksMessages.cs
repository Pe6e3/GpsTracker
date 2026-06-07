namespace GpsTcpProxy.Models;

public sealed class OwnTracksLocationMessage
{
    public required string DeviceId { get; init; }
    public required string Topic { get; init; }
    public string? MqttUser { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double? Accuracy { get; init; }
    public int? Battery { get; init; }
    public DateTime TimestampUtc { get; init; }
    public double? VelocityKmh { get; init; }
    public int? Altitude { get; init; }
    public int? Course { get; init; }
    public string? TrackerId { get; init; }
}

public sealed class OwnTracksStatusMessage
{
    public required string DeviceId { get; init; }
    public required string Topic { get; init; }
    public string? MqttUser { get; init; }
    public int? Battery { get; init; }
    public string? Payload { get; init; }
}

public sealed class OwnTracksTopicInfo
{
    public required string DeviceId { get; init; }
    public required string MqttUser { get; init; }
    public required string TopicBase { get; init; }

    public string CommandTopic => $"{TopicBase}/cmd";
}
