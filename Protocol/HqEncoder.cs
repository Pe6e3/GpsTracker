using System.Text;

namespace GpsTcpProxy.Protocol;

public static class HqEncoder
{
    public static byte[] BuildR12Ack(string deviceId, DateTime localTime)
    {
        var time = localTime.ToString("HHmmss");
        var text = $"*HQ,{deviceId},{HqParser.PacketTypeAck},{time}#";
        return Encoding.ASCII.GetBytes(text);
    }
}
