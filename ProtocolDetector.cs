using GpsTcpProxy.Models;
using GpsTcpProxy.Protocol;

namespace GpsTcpProxy;

public sealed class ProtocolDetectionResult
{
    public required DeviceProtocol Protocol { get; init; }
    public required string DeviceId { get; init; }
    public bool FromRegistry { get; init; }
}

public static class ProtocolDetector
{
    public static ProtocolDetectionResult? TryDetect(
        IReadOnlyList<byte[]> jt808Frames,
        IReadOnlyList<byte[]> gt06Frames,
        IReadOnlyList<byte[]>? hqFrames = null)
    {
        var hqDetection = hqFrames is { Count: > 0 } ? TryDetectHq(hqFrames) : null;
        if (hqDetection != null)
            return hqDetection;

        string? jt808DeviceId = null;
        string? gt06DeviceId = null;

        foreach (var frame in jt808Frames)
        {
            if (!Jt808Parser.TryParseFrame(frame, out var message) || message == null)
                continue;

            if (string.IsNullOrWhiteSpace(message.TerminalId))
                continue;

            jt808DeviceId ??= message.TerminalId;
        }

        foreach (var frame in gt06Frames)
        {
            if (!Gt06Parser.TryParseFrame(frame, out var message) || message == null)
                continue;

            if (message.ProtocolNumber != Gt06Parser.ProtocolLogin || string.IsNullOrWhiteSpace(message.Imei))
                continue;

            gt06DeviceId = message.Imei;
        }

        if (!string.IsNullOrWhiteSpace(jt808DeviceId) && !string.IsNullOrWhiteSpace(gt06DeviceId))
        {
            var jt808Norm = DeviceRegistry.NormalizeId(jt808DeviceId);
            var gt06Norm = DeviceRegistry.NormalizeId(gt06DeviceId);
            if (jt808Norm != gt06Norm)
                return ResolveByValidFrames(jt808Frames, gt06Frames, string.Empty);
        }

        var deviceId = gt06DeviceId ?? jt808DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var registryProtocol = DeviceRegistry.GetProtocolOrNull(deviceId);
        if (registryProtocol.HasValue)
        {
            return new ProtocolDetectionResult
            {
                Protocol = registryProtocol.Value,
                DeviceId = deviceId,
                FromRegistry = true
            };
        }

        if (!string.IsNullOrWhiteSpace(gt06DeviceId))
        {
            return new ProtocolDetectionResult
            {
                Protocol = DeviceProtocol.Gt06,
                DeviceId = gt06DeviceId,
                FromRegistry = false
            };
        }

        if (!string.IsNullOrWhiteSpace(jt808DeviceId))
        {
            return new ProtocolDetectionResult
            {
                Protocol = DeviceProtocol.Jt808,
                DeviceId = jt808DeviceId,
                FromRegistry = false
            };
        }

        return null;
    }

    public static ProtocolDetectionResult? TryDetectProtocolOnly(
        IReadOnlyList<byte[]> jt808Frames,
        IReadOnlyList<byte[]> gt06Frames,
        IReadOnlyList<byte[]>? hqFrames = null)
    {
        var hqDetection = hqFrames is { Count: > 0 } ? TryDetectHq(hqFrames) : null;
        if (hqDetection != null)
            return hqDetection;

        var full = TryDetect(jt808Frames, gt06Frames);
        if (full != null)
            return full;

        return ResolveByValidFrames(jt808Frames, gt06Frames, string.Empty);
    }

    public static DeviceProtocol GuessProtocol(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == (byte)'*' && data[1] == (byte)'H' && data[2] == (byte)'Q')
            return DeviceProtocol.Hq;

        if (data.Length > 0 && data[0] == HqParser.BinaryFrameMarker)
            return DeviceProtocol.Hq;

        for (var i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == 0x78 && data[i + 1] == 0x78)
                return DeviceProtocol.Gt06;
        }

        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == 0x7E)
                return DeviceProtocol.Jt808;
        }

        return DeviceProtocol.Jt808;
    }

    private static ProtocolDetectionResult? ResolveByValidFrames(
        IReadOnlyList<byte[]> jt808Frames,
        IReadOnlyList<byte[]> gt06Frames,
        string deviceId)
    {
        var validJt808 = CountValidJt808Frames(jt808Frames);
        var validGt06 = CountValidGt06Frames(gt06Frames);

        if (validGt06 > 0 && validJt808 == 0)
        {
            return new ProtocolDetectionResult
            {
                Protocol = DeviceProtocol.Gt06,
                DeviceId = deviceId,
                FromRegistry = false
            };
        }

        if (validJt808 > 0 && validGt06 == 0)
        {
            return new ProtocolDetectionResult
            {
                Protocol = DeviceProtocol.Jt808,
                DeviceId = deviceId,
                FromRegistry = false
            };
        }

        if (validGt06 > 0 && validJt808 > 0)
        {
            return new ProtocolDetectionResult
            {
                Protocol = validGt06 >= validJt808 ? DeviceProtocol.Gt06 : DeviceProtocol.Jt808,
                DeviceId = deviceId,
                FromRegistry = false
            };
        }

        return null;
    }

    public static ProtocolDetectionResult? TryDetectHq(IReadOnlyList<byte[]> hqFrames)
    {
        foreach (var frame in hqFrames)
        {
            if (!HqParser.TryParseFrame(frame, out var message) || message == null)
                continue;

            if (string.IsNullOrWhiteSpace(message.DeviceId))
                continue;

            var registryProtocol = DeviceRegistry.GetProtocolOrNull(message.DeviceId);
            return new ProtocolDetectionResult
            {
                Protocol = registryProtocol == DeviceProtocol.Hq || registryProtocol == null
                    ? DeviceProtocol.Hq
                    : registryProtocol.Value,
                DeviceId = message.DeviceId,
                FromRegistry = registryProtocol == DeviceProtocol.Hq
            };
        }

        return null;
    }

    private static int CountValidJt808Frames(IReadOnlyList<byte[]> frames)
    {
        var count = 0;
        foreach (var frame in frames)
        {
            if (Jt808Parser.TryParseFrame(frame, out var message) && message != null && message.ChecksumValid)
                count++;
        }

        return count;
    }

    private static int CountValidGt06Frames(IReadOnlyList<byte[]> frames)
    {
        var count = 0;
        foreach (var frame in frames)
        {
            if (Gt06Parser.TryParseFrame(frame, out var message) && message != null && message.ChecksumValid)
                count++;
        }

        return count;
    }
}
