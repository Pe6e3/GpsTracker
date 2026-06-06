namespace GpsTcpProxy;

public static class LogFiles
{
    private static RotatingLogWriter? _traffic;
    private static RotatingLogWriter? _raw;

    public static void Configure(int retentionDays = 10)
    {
        var logsRoot = Path.Combine(AppContext.BaseDirectory, "logs");
        _traffic = new RotatingLogWriter(logsRoot, retentionDays);
        _raw = new RotatingLogWriter(Path.Combine(logsRoot, "raw_data"), retentionDays);
        _traffic.CheckRotation();
        _raw.CheckRotation();
    }

    public static RotatingLogWriter Traffic =>
        _traffic ?? throw new InvalidOperationException("LogFiles не инициализирован");

    public static RotatingLogWriter Raw =>
        _raw ?? throw new InvalidOperationException("LogFiles не инициализирован");

    public static void CheckRotation()
    {
        _traffic?.CheckRotation();
        _raw?.CheckRotation();
    }
}
