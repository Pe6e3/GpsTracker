namespace GpsTcpProxy.Protocol;

public static class Jt808Escape
{
    public static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return Array.Empty<byte>();

        var result = new List<byte>(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != 0x7D || i + 1 >= data.Length)
            {
                result.Add(data[i]);
                continue;
            }

            if (data[i + 1] == 0x02)
            {
                result.Add(0x7E);
                i++;
                continue;
            }

            if (data[i + 1] == 0x01)
            {
                result.Add(0x7D);
                i++;
                continue;
            }

            result.Add(data[i]);
        }

        return result.ToArray();
    }
}
