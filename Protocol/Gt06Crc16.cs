namespace GpsTcpProxy.Protocol;

public static class Gt06Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;

        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                if ((crc & 0x0001) != 0)
                    crc = (ushort)((crc >> 1) ^ 0x8408);
                else
                    crc >>= 1;
            }
        }

        return (ushort)~crc;
    }

    public static bool ValidateFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 7 || frame[0] != 0x78 || frame[1] != 0x78)
            return false;

        var contentLength = frame[2];
        if (contentLength < 3)
            return false;

        var frameLength = contentLength + 5;
        if (frame.Length < frameLength)
            return false;

        if (frame[frameLength - 2] != 0x0D || frame[frameLength - 1] != 0x0A)
            return false;

        var crcOffset = contentLength + 1;
        var expectedCrc = (ushort)((frame[crcOffset] << 8) | frame[crcOffset + 1]);
        var actualCrc = Compute(frame.Slice(2, contentLength - 1));
        return expectedCrc == actualCrc;
    }
}
