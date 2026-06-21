using System.Globalization;

namespace GpsTcpProxy.Protocol;

public static class HqParser
{
    public const string PacketTypeTelemetry = "V1";
    public const string PacketTypeAck = "R12";
    public const byte BinaryFrameMarker = 0x24;
    public const int BinaryFrameLength = 45;

    public static bool TryParseFrame(byte[] rawFrame, out HqMessage? message)
    {
        message = null;
        if (rawFrame.Length == 0)
            return false;

        if (rawFrame[0] == BinaryFrameMarker)
            return TryParseBinaryFrame(rawFrame, out message);

        return TryParseTextFrame(rawFrame, out message);
    }

    public static bool TryParseTextFrame(byte[] rawFrame, out HqMessage? message)
    {
        message = null;
        var text = System.Text.Encoding.ASCII.GetString(rawFrame).Trim();
        if (!text.StartsWith("*HQ,", StringComparison.Ordinal) || !text.EndsWith('#'))
            return false;

        var body = text[1..^1];
        var fields = body.Split(',');
        if (fields.Length < 3 || !fields[0].Equals("HQ", StringComparison.OrdinalIgnoreCase))
            return false;

        var deviceId = fields[1].Trim();
        var packetType = fields[2].Trim();
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(packetType))
            return false;

        HqLocationReport? location = null;
        if (packetType.Equals(PacketTypeTelemetry, StringComparison.OrdinalIgnoreCase))
            location = TryParseV1Location(fields);

        message = new HqMessage
        {
            DeviceId = deviceId,
            PacketType = packetType.ToUpperInvariant(),
            RawFrame = rawFrame,
            IsBinary = false,
            Location = location
        };

        return true;
    }

    private static bool TryParseBinaryFrame(byte[] rawFrame, out HqMessage? message)
    {
        message = null;
        if (rawFrame.Length < BinaryFrameLength)
            return false;

        var deviceId = DecodeBcdDeviceId(rawFrame.AsSpan(1, 5));
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        message = new HqMessage
        {
            DeviceId = deviceId,
            PacketType = "BIN",
            RawFrame = rawFrame.Length == BinaryFrameLength ? rawFrame : rawFrame[..BinaryFrameLength],
            IsBinary = true,
            Location = TryParseBinaryLocation(rawFrame)
        };

        return true;
    }

    private static HqLocationReport? TryParseV1Location(string[] fields)
    {
        if (fields.Length < 12)
            return null;

        var gpsStatus = fields[4].Trim();
        if (!gpsStatus.Equals("A", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!TryParseDeviceDateTime(fields[3], fields[11], out var deviceTimeUtc))
            return null;

        if (!TryParseCoordinate(fields[5], fields[6], out var latitude))
            return null;

        if (!TryParseCoordinate(fields[7], fields[8], out var longitude))
            return null;

        _ = double.TryParse(fields[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var speedKmh);
        _ = int.TryParse(fields[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var direction);

        if (TelemetryStore.IsImplausibleSpeed(speedKmh))
            return null;

        return new HqLocationReport
        {
            DeviceTimeUtc = deviceTimeUtc,
            Latitude = latitude,
            Longitude = longitude,
            SpeedKmh = speedKmh,
            Direction = direction,
            GpsValid = true
        };
    }

    private static HqLocationReport? TryParseBinaryLocation(byte[] rawFrame)
    {
        if (rawFrame.Length < BinaryFrameLength)
            return null;

        if (!TryParseDeviceDateTimeFromBcd(rawFrame.AsSpan(6, 3), rawFrame.AsSpan(9, 3), out var deviceTimeUtc))
            return null;

        var latitude = ParsePackedBcdCoordinate(rawFrame.AsSpan(12, 4), isLongitude: false);
        var longitude = ParsePackedBcdCoordinate(rawFrame.AsSpan(17, 4), isLongitude: true);
        if (latitude == null || longitude == null)
            return null;

        var speedKmh = DecodeBcdByte(rawFrame[21]) + DecodeBcdByte(rawFrame[22]) / 100d;
        var direction = DecodeBcdByte(rawFrame[23]) * 100 + DecodeBcdByte(rawFrame[24]);

        if (TelemetryStore.IsImplausibleSpeed(speedKmh))
            return null;

        return new HqLocationReport
        {
            DeviceTimeUtc = deviceTimeUtc,
            Latitude = latitude.Value,
            Longitude = longitude.Value,
            SpeedKmh = speedKmh,
            Direction = direction,
            GpsValid = true
        };
    }

    private static bool TryParseDeviceDateTime(string timeText, string dateText, out DateTime deviceTimeUtc)
    {
        deviceTimeUtc = default;
        if (timeText.Length != 6 || dateText.Length != 6)
            return false;

        if (!int.TryParse(timeText[..2], out var hour)
            || !int.TryParse(timeText.Substring(2, 2), out var minute)
            || !int.TryParse(timeText.Substring(4, 2), out var second)
            || !int.TryParse(dateText[..2], out var day)
            || !int.TryParse(dateText.Substring(2, 2), out var month)
            || !int.TryParse(dateText.Substring(4, 2), out var year))
            return false;

        try
        {
            var deviceLocal = new DateTime(2000 + year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            deviceTimeUtc = AppTime.DeviceTimeToUtc(deviceLocal);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseDeviceDateTimeFromBcd(ReadOnlySpan<byte> timeBytes, ReadOnlySpan<byte> dateBytes, out DateTime deviceTimeUtc)
    {
        deviceTimeUtc = default;
        if (timeBytes.Length < 3 || dateBytes.Length < 3)
            return false;

        try
        {
            var hour = DecodeBcdByte(timeBytes[0]);
            var minute = DecodeBcdByte(timeBytes[1]);
            var second = DecodeBcdByte(timeBytes[2]);
            var day = DecodeBcdByte(dateBytes[0]);
            var month = DecodeBcdByte(dateBytes[1]);
            var year = DecodeBcdByte(dateBytes[2]);
            var deviceLocal = new DateTime(2000 + year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            deviceTimeUtc = AppTime.DeviceTimeToUtc(deviceLocal);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseCoordinate(string rawValue, string hemisphere, out double coordinate)
    {
        coordinate = 0;
        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var packed))
            return false;

        var degrees = (int)(packed / 100);
        var minutes = packed - degrees * 100;
        coordinate = degrees + minutes / 60d;

        if (hemisphere.Equals("S", StringComparison.OrdinalIgnoreCase)
            || hemisphere.Equals("W", StringComparison.OrdinalIgnoreCase))
            coordinate = -coordinate;

        return true;
    }

    private static double? ParsePackedBcdCoordinate(ReadOnlySpan<byte> data, bool isLongitude)
    {
        if (data.Length < 4)
            return null;

        var digits = new System.Text.StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            digits.Append((b >> 4) & 0x0F);
            digits.Append(b & 0x0F);
        }

        var text = digits.ToString();
        var degreeDigits = isLongitude ? 3 : 2;
        if (text.Length < degreeDigits + 2)
            return null;

        if (!int.TryParse(text[..degreeDigits], NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees))
            return null;

        if (!int.TryParse(text.Substring(degreeDigits, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutesWhole))
            return null;

        var fractionLength = text.Length - degreeDigits - 2;
        var minutesFraction = 0d;
        if (fractionLength > 0
            && int.TryParse(text[(degreeDigits + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fraction))
            minutesFraction = fraction / Math.Pow(10, fractionLength);

        return degrees + (minutesWhole + minutesFraction) / 60d;
    }

    private static string DecodeBcdDeviceId(ReadOnlySpan<byte> data)
    {
        var digits = new System.Text.StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            digits.Append((b >> 4) & 0x0F);
            digits.Append(b & 0x0F);
        }

        return DeviceRegistry.NormalizeId(digits.ToString());
    }

    private static int DecodeBcdByte(byte value) =>
        ((value >> 4) & 0x0F) * 10 + (value & 0x0F);
}
