namespace GpsTcpProxy.Protocol;

public static class Jt808Parser
{
    public const ushort MsgRegistration = 0x0100;
    public const ushort MsgRegistrationResponse = 0x8100;
    public const ushort MsgAuthentication = 0x0102;
    public const ushort MsgTimeSyncRequest = 0x0104;
    public const ushort MsgHeartbeat = 0x0002;
    public const ushort MsgLocationReport = 0x0200;
    public const ushort MsgGeneralResponse = 0x8001;
    public const ushort MsgTimeSyncResponse = 0x8104;
    public const ushort MsgLocationQuery = 0x8201;

    public static bool TryParseFrame(byte[] rawFrame, out Jt808Message? message)
    {
        message = null;
        if (rawFrame.Length < 15 || rawFrame[0] != 0x7E || rawFrame[^1] != 0x7E)
            return false;

        var payload = Jt808Escape.Unescape(rawFrame.AsSpan(1, rawFrame.Length - 2));
        if (payload.Length < 13)
            return false;

        var messageId = ReadUInt16(payload, 0);
        var properties = ReadUInt16(payload, 2);
        var bodyLength = properties & 0x03FF;
        var expectedLength = 12 + bodyLength + 1;
        if (payload.Length < expectedLength)
            return false;

        var terminalId = Jt808Bcd.DecodeDigits(payload.AsSpan(4, 6));
        var serial = ReadUInt16(payload, 10);
        var body = payload.AsSpan(12, bodyLength).ToArray();
        var checksum = payload[12 + bodyLength];
        var calculated = XorChecksum(payload.AsSpan(0, 12 + bodyLength));

        message = new Jt808Message
        {
            MessageId = messageId,
            Properties = properties,
            TerminalId = terminalId,
            Serial = serial,
            Body = body,
            Checksum = checksum,
            CalculatedChecksum = calculated,
            ChecksumValid = checksum == calculated,
            RawFrame = rawFrame
        };

        return true;
    }

    public static bool TryGetTerminalIdBytes(byte[] rawFrame, out byte[] terminalIdBytes)
    {
        terminalIdBytes = Array.Empty<byte>();
        if (rawFrame.Length < 15 || rawFrame[0] != 0x7E || rawFrame[^1] != 0x7E)
            return false;

        var payload = Jt808Escape.Unescape(rawFrame.AsSpan(1, rawFrame.Length - 2));
        if (payload.Length < 12)
            return false;

        terminalIdBytes = payload.AsSpan(4, 6).ToArray();
        return true;
    }

    public static Jt808LocationReport? ParseLocationReport(ReadOnlySpan<byte> body)
    {
        if (body.Length < 28)
            return null;

        var lat = ReadUInt32(body, 8);
        var lon = ReadUInt32(body, 12);

        var report = new Jt808LocationReport
        {
            AlarmFlags = ReadUInt32(body, 0),
            StatusFlags = ReadUInt32(body, 4),
            Latitude = lat / 1_000_000d,
            Longitude = lon / 1_000_000d,
            Altitude = ReadUInt16(body, 16),
            SpeedKmh = ReadUInt16(body, 18) / 10d,
            Direction = ReadUInt16(body, 20),
            Time = Jt808Bcd.DecodeDateTime(body.Slice(22, 6)),
            Extra = ParseTlvs(body.Slice(28))
        };

        return report;
    }

    public static Jt808GeneralResponse? ParseGeneralResponse(ReadOnlySpan<byte> body)
    {
        if (body.Length < 5)
            return null;

        return new Jt808GeneralResponse
        {
            OriginalSerial = ReadUInt16(body, 0),
            OriginalMessageId = ReadUInt16(body, 2),
            Result = body[4]
        };
    }

    public static string FormatMessageId(ushort messageId) => messageId switch
    {
        MsgRegistration => "0x0100 Регистрация",
        MsgRegistrationResponse => "0x8100 Ответ на регистрацию",
        MsgAuthentication => "0x0102 Аутентификация",
        MsgHeartbeat => "0x0002 Heartbeat",
        MsgLocationReport => "0x0200 Координаты",
        MsgGeneralResponse => "0x8001 Общий ответ",
        _ => $"0x{messageId:X4}"
    };

    public static string FormatResult(byte result) => result switch
    {
        0 => "успех",
        1 => "ошибка",
        2 => "ошибка сообщения",
        3 => "не поддерживается",
        4 => "подтверждение тревоги",
        _ => $"код {result}"
    };

    private static List<Jt808Tlv> ParseTlvs(ReadOnlySpan<byte> data)
    {
        var tlvs = new List<Jt808Tlv>();
        var i = 0;

        while (i + 2 <= data.Length)
        {
            var id = data[i];
            var length = data[i + 1];
            if (i + 2 + length > data.Length)
                break;

            tlvs.Add(new Jt808Tlv
            {
                Id = id,
                Value = data.Slice(i + 2, length).ToArray()
            });

            i += 2 + length;
        }

        return tlvs;
    }

    private static byte XorChecksum(ReadOnlySpan<byte> data)
    {
        byte checksum = 0;
        foreach (var b in data)
            checksum ^= b;
        return checksum;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) |
        ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) |
        data[offset + 3];
}
