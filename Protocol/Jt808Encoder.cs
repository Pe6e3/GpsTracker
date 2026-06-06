using System.Text;

namespace GpsTcpProxy.Protocol;

public static class Jt808Encoder
{
    public static byte[] BuildGeneralResponse(
        string terminalId,
        ushort serverSerial,
        ushort originalSerial,
        ushort originalMessageId,
        byte result = 0)
    {
        var body = new byte[5];
        WriteUInt16(body, 0, originalSerial);
        WriteUInt16(body, 2, originalMessageId);
        body[4] = result;
        return BuildFrame(Jt808Parser.MsgGeneralResponse, terminalId, serverSerial, body);
    }

    public static byte[] BuildRegistrationResponse(
        string terminalId,
        ushort serverSerial,
        ushort originalSerial,
        byte result,
        string authCode)
    {
        var authBytes = Encoding.ASCII.GetBytes(authCode);
        var body = new byte[3 + authBytes.Length];
        WriteUInt16(body, 0, originalSerial);
        body[2] = result;
        authBytes.CopyTo(body, 3);
        return BuildFrame(Jt808Parser.MsgRegistrationResponse, terminalId, serverSerial, body);
    }

    private static byte[] BuildFrame(ushort messageId, string terminalId, ushort serial, ReadOnlySpan<byte> body)
    {
        var properties = (ushort)(body.Length & 0x03FF);
        var header = new byte[12];
        WriteUInt16(header, 0, messageId);
        WriteUInt16(header, 2, properties);
        Jt808Bcd.EncodeTerminalId(terminalId).CopyTo(header, 4);
        WriteUInt16(header, 10, serial);

        var payload = new byte[12 + body.Length + 1];
        header.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(12));
        payload[12 + body.Length] = XorChecksum(payload.AsSpan(0, 12 + body.Length));

        return WrapFrame(payload);
    }

    private static byte[] WrapFrame(ReadOnlySpan<byte> payload)
    {
        var escaped = Jt808Escape.Escape(payload);
        var frame = new byte[escaped.Length + 2];
        frame[0] = 0x7E;
        escaped.CopyTo(frame, 1);
        frame[^1] = 0x7E;
        return frame;
    }

    private static byte XorChecksum(ReadOnlySpan<byte> data)
    {
        byte checksum = 0;
        foreach (var b in data)
            checksum ^= b;
        return checksum;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }
}
