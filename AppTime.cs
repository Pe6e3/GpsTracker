namespace GpsTcpProxy;

public static class AppTime
{
    private static int _utcOffset = 5;
    private static int _deviceUtcOffset = 8;

    public static int UtcOffset => _utcOffset;
    public static int DeviceUtcOffset => _deviceUtcOffset;

    public static void Configure(ProxySettings settings)
    {
        _utcOffset = settings.UtcOffset;
        _deviceUtcOffset = settings.DeviceUtcOffset;
    }

    public static DateTime NowLocal() =>
        DateTime.UtcNow.AddHours(_utcOffset);

    public static DateTime UtcToLocal(DateTime utc) =>
        utc.AddHours(_utcOffset);

    public static DateTime DeviceTimeToLocal(DateTime deviceTime)
    {
        var utc = deviceTime.AddHours(-_deviceUtcOffset);
        return utc.AddHours(_utcOffset);
    }
}
