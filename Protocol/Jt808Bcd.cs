namespace GpsTcpProxy.Protocol;

public static class Jt808Bcd
{
    public static byte[] EncodeTerminalId(string terminalId)
    {
        var digits = new string(terminalId.Where(char.IsDigit).ToArray());
        if (digits.Length > 12)
            digits = digits[^12..];

        digits = digits.PadLeft(12, '0');

        var result = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            var high = digits[i * 2] - '0';
            var low = digits[i * 2 + 1] - '0';
            result[i] = (byte)((high << 4) | low);
        }

        return result;
    }

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

    public static bool TryParseDateTime(ReadOnlySpan<byte> data, out DateTime value)
    {
        value = default;
        if (data.Length < 6)
            return false;

        try
        {
            value = new DateTime(
                2000 + BcdNibble(data[0]),
                BcdNibble(data[1]),
                BcdNibble(data[2]),
                BcdNibble(data[3]),
                BcdNibble(data[4]),
                BcdNibble(data[5]));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int BcdNibble(byte value) => ((value >> 4) & 0x0F) * 10 + (value & 0x0F);

    public static string DecodeDateTime(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
            return BitConverter.ToString(data.ToArray());

        return $"20{data[0]:X2}-{data[1]:X2}-{data[2]:X2} {data[3]:X2}:{data[4]:X2}:{data[5]:X2}";
    }

    public static byte[] EncodeDateTime(DateTime time)
    {
        var year = time.Year % 100;
        return new[]
        {
            ToBcdByte(year),
            ToBcdByte(time.Month),
            ToBcdByte(time.Day),
            ToBcdByte(time.Hour),
            ToBcdByte(time.Minute),
            ToBcdByte(time.Second)
        };
    }

    private static byte ToBcdByte(int value) =>
        (byte)(((value / 10) << 4) | (value % 10));
}
