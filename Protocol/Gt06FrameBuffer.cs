namespace GpsTcpProxy.Protocol;

public sealed class Gt06FrameBuffer
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
            var start = FindStart(_buffer);
            if (start < 0)
            {
                if (_buffer.Count > 1)
                    _buffer.RemoveRange(0, _buffer.Count - 1);
                break;
            }

            if (start > 0)
                _buffer.RemoveRange(0, start);

            if (_buffer.Count < 5)
                break;

            var contentLength = _buffer[2];
            var frameLength = contentLength + 5;
            if (_buffer.Count < frameLength)
                break;

            var frame = _buffer.GetRange(0, frameLength).ToArray();
            if (frame[^2] == 0x0D && frame[^1] == 0x0A && Gt06Crc16.ValidateFrame(frame))
            {
                frames.Add(frame);
                _buffer.RemoveRange(0, frameLength);
                continue;
            }

            _buffer.RemoveAt(0);
        }

        return frames;
    }

    private static int FindStart(IReadOnlyList<byte> buffer)
    {
        for (var i = 0; i + 1 < buffer.Count; i++)
        {
            if (buffer[i] == 0x78 && buffer[i + 1] == 0x78)
                return i;
        }

        return -1;
    }
}
