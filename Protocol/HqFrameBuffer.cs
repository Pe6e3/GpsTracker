namespace GpsTcpProxy.Protocol;

public sealed class HqFrameBuffer
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

        while (_buffer.Count > 0)
        {
            if (_buffer[0] == HqParser.BinaryFrameMarker)
            {
                if (_buffer.Count < HqParser.BinaryFrameLength)
                    break;

                frames.Add(_buffer.GetRange(0, HqParser.BinaryFrameLength).ToArray());
                _buffer.RemoveRange(0, HqParser.BinaryFrameLength);
                continue;
            }

            var start = FindTextStart(_buffer);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }

            if (start > 0)
                _buffer.RemoveRange(0, start);

            var end = FindTextEnd(_buffer);
            if (end < 0)
                break;

            frames.Add(_buffer.GetRange(0, end + 1).ToArray());
            _buffer.RemoveRange(0, end + 1);
        }

        return frames;
    }

    private static int FindTextStart(IReadOnlyList<byte> buffer)
    {
        for (var i = 0; i + 2 < buffer.Count; i++)
        {
            if (buffer[i] == (byte)'*' && buffer[i + 1] == (byte)'H' && buffer[i + 2] == (byte)'Q')
                return i;
        }

        return -1;
    }

    private static int FindTextEnd(IReadOnlyList<byte> buffer)
    {
        for (var i = 0; i < buffer.Count; i++)
        {
            if (buffer[i] == (byte)'#')
                return i;
        }

        return -1;
    }
}
