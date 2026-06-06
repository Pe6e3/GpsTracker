namespace GpsTcpProxy.Protocol;

public sealed class Jt808Message
{
    public required ushort MessageId { get; init; }
    public required ushort Properties { get; init; }
    public required string TerminalId { get; init; }
    public required ushort Serial { get; init; }
    public required byte[] Body { get; init; }
    public required byte Checksum { get; init; }
    public required byte CalculatedChecksum { get; init; }
    public required bool ChecksumValid { get; init; }
    public required byte[] RawFrame { get; init; }

    public int BodyLength => Properties & 0x03FF;
    public bool HasSubPackage => (Properties & 0x2000) != 0;
    public bool IsEncrypted => (Properties & 0x0400) != 0;
}

public sealed class Jt808LocationReport
{
    public uint AlarmFlags { get; init; }
    public uint StatusFlags { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public ushort Altitude { get; init; }
    public double SpeedKmh { get; init; }
    public ushort Direction { get; init; }
    public string Time { get; init; } = string.Empty;
    public IReadOnlyList<Jt808Tlv> Extra { get; init; } = Array.Empty<Jt808Tlv>();
}

public sealed class Jt808Tlv
{
    public byte Id { get; init; }
    public byte[] Value { get; init; } = Array.Empty<byte>();
}

public sealed class Jt808GeneralResponse
{
    public ushort OriginalSerial { get; init; }
    public ushort OriginalMessageId { get; init; }
    public byte Result { get; init; }
}
