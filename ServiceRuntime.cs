namespace GpsTcpProxy;

public static class ServiceRuntime
{
    public static DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    public static long UptimeSeconds =>
        (long)Math.Max(0, (DateTime.UtcNow - StartedAtUtc).TotalSeconds);
}
