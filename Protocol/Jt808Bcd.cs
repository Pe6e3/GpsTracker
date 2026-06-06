namespace GpsTcpProxy.Protocol;

public static class Jt808Bcd
{
    public static string DecodeDigits(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return string.Empty;

        var chars = new char[data.Length * 2];
        var n = 0;
        foreach (var b in data)
        {
            chars[n++] = (char)('0' + ((b >> 4) & 0x0F));
            chars[n++] = (char)('0' + (b & 0x0F));
        }

        var result = new string(chars).TrimStart('0');
        return result.Length == 0 ? "0" : result;
    }

    public static string DecodeDateTime(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
            return BitConverter.ToString(data.ToArray());

        return $"20{data[0]:X2}-{data[1]:X2}-{data[2]:X2} {data[3]:X2}:{data[4]:X2}:{data[5]:X2}";
    }
}
