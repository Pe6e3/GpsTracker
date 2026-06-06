namespace GpsTcpProxy;

public static class AppTime
{
    private static int _utcOffset = 5;
    private static int _serverUtcOffset = 2;
    private static int _deviceUtcOffset = 8;

    public static int UtcOffset => _utcOffset;
    public static int ServerUtcOffset => _serverUtcOffset;
    public static int DeviceUtcOffset => _deviceUtcOffset;

    public static void Configure(ProxySettings settings)
    {
        _utcOffset = settings.UtcOffset;
        _serverUtcOffset = settings.ServerUtcOffset;
        _deviceUtcOffset = settings.DeviceUtcOffset;
    }

    public static DateTime NowLocal() =>
        DateTime.Now.AddHours(_utcOffset - _serverUtcOffset);

    public static DateTime UtcToLocal(DateTime utc) =>
        utc.AddHours(_utcOffset);

    public static DateTime LocalToUtc(DateTime local) =>
        local.AddHours(-_utcOffset);

    public static DateTime DeviceTimeToLocal(DateTime deviceTime)
    {
        var utc = DeviceTimeToUtc(deviceTime);
        return utc.AddHours(_utcOffset);
    }

    public static DateTime DeviceTimeToUtc(DateTime deviceTime) =>
        AsUtc(deviceTime.AddHours(-_deviceUtcOffset));

    public static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static string FormatUtcOffset(int offset) =>
        $"UTC{(offset >= 0 ? "+" : "")}{offset}";
}
