namespace GpsTcpProxy.Protocol;

public sealed class Jt808FrameBuffer
{
    private readonly List<byte> _buffer = new();

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        foreach (var b in data)
            _buffer.Add(b);
    }

    public IReadOnlyList<byte[]> ExtractFrames()
    {
        var frames = new List<byte[]>();

        while (true)
        {
            var start = IndexOf(_buffer, 0x7E);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }

            if (start > 0)
                _buffer.RemoveRange(0, start);

            var end = FindFrameEnd(_buffer, 1);
            if (end < 0)
                break;

            var frame = _buffer.GetRange(0, end + 1).ToArray();
            frames.Add(frame);
            _buffer.RemoveRange(0, end + 1);
        }

        return frames;
    }

    private static int FindFrameEnd(IReadOnlyList<byte> buffer, int startIndex)
    {
        for (var i = startIndex; i < buffer.Count; i++)
        {
            if (buffer[i] != 0x7D)
            {
                if (buffer[i] == 0x7E)
                    return i;
                continue;
            }

            if (i + 1 < buffer.Count && (buffer[i + 1] == 0x02 || buffer[i + 1] == 0x01))
            {
                i++;
                continue;
            }

            if (buffer[i] == 0x7E)
                return i;
        }

        return -1;
    }

    private static int IndexOf(IReadOnlyList<byte> buffer, byte value)
    {
        for (var i = 0; i < buffer.Count; i++)
        {
            if (buffer[i] == value)
                return i;
        }

        return -1;
    }
}
