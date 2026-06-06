namespace GpsTcpProxy.Protocol;

public static class Gt06Encoder
{
    public static byte[] BuildAck(byte protocolNumber, ushort serial)
    {
        Span<byte> crcData = stackalloc byte[4];
        crcData[0] = 0x05;
        crcData[1] = protocolNumber;
        crcData[2] = (byte)(serial >> 8);
        crcData[3] = (byte)(serial & 0xFF);

        var crc = Gt06Crc16.Compute(crcData);

        return new byte[]
        {
            0x78, 0x78,
            0x05,
            protocolNumber,
            crcData[2],
            crcData[3],
            (byte)(crc >> 8),
            (byte)(crc & 0xFF),
            0x0D, 0x0A
        };
    }
}
