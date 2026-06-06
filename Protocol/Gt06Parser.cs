namespace GpsTcpProxy.Protocol;

public static class Gt06Parser
{
    public const byte ProtocolLogin = 0x01;
    public const byte ProtocolLocation = 0x22;
    public const byte ProtocolHeartbeat = 0x13;
    public const byte ProtocolStatus = 0x12;

    public static bool TryParseFrame(byte[] rawFrame, out Gt06Message? message)
    {
        message = null;
        if (!Gt06Crc16.ValidateFrame(rawFrame))
            return false;

        var contentLength = rawFrame[2];
        var protocol = rawFrame[3];
        var bodyLength = contentLength - 3;
        if (bodyLength < 0)
            return false;

        var crcOffset = contentLength + 1;
        var serial = (ushort)((rawFrame[crcOffset - 2] << 8) | rawFrame[crcOffset - 1]);
        var payload = rawFrame.AsSpan(4, bodyLength).ToArray();

        message = new Gt06Message
        {
            ProtocolNumber = protocol,
            ContentLength = contentLength,
            Payload = payload,
            Serial = serial,
            RawFrame = rawFrame,
            ChecksumValid = true,
            Imei = protocol == ProtocolLogin ? TryParseLoginImei(payload) : null
        };

        return true;
    }

    public static string? TryParseLoginImei(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            return null;

        var imei = Jt808Bcd.DecodeDigits(payload.Slice(0, 8));
        return string.IsNullOrWhiteSpace(imei) ? null : imei;
    }

    public static Gt06LocationReport? ParseLocationReport(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 18)
            return null;

        if (!TryParseDateTimeUtc(payload.Slice(0, 6), out var deviceTimeUtc))
            return null;

        var gpsInfo = payload[6];
        var satelliteCount = gpsInfo & 0x0F;
        var latitude = ReadCoordinate(payload.Slice(7, 4));
        var longitude = ReadCoordinate(payload.Slice(11, 4));
        var speedKmh = payload[15];
        var courseRaw = (ushort)((payload[16] << 8) | payload[17]);
        var direction = courseRaw & 0x03FF;

        return new Gt06LocationReport
        {
            DeviceTimeUtc = deviceTimeUtc,
            Latitude = latitude,
            Longitude = longitude,
            SpeedKmh = speedKmh,
            Direction = direction,
            SatelliteCount = satelliteCount
        };
    }

    public static bool TryParseDateTimeUtc(ReadOnlySpan<byte> data, out DateTime value)
    {
        value = default;
        if (data.Length < 6)
            return false;

        try
        {
            var year = 2000 + data[0];
            var month = data[1];
            var day = data[2];
            var hour = data[3];
            var minute = data[4];
            var second = data[5];
            value = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string GetProtocolName(byte protocolNumber) => protocolNumber switch
    {
        ProtocolLogin => "0x01 Login",
        ProtocolLocation => "0x22 GPS",
        ProtocolHeartbeat => "0x13 Heartbeat",
        ProtocolStatus => "0x12 Status",
        _ => $"0x{protocolNumber:X2}"
    };

    private static double ReadCoordinate(ReadOnlySpan<byte> data)
    {
        var raw = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        return raw / 1_800_000d;
    }
}
