namespace GpsTcpProxy;

public static class PlatformSerial
{
    private const int MinSerial = 0x0100;
    private static readonly object Lock = new();
    private static string _storePath = string.Empty;
    private static int _current;

    public static void Initialize(string databasePath)
    {
        var fullDbPath = Path.IsPathRooted(databasePath)
            ? databasePath
            : Path.Combine(AppContext.BaseDirectory, databasePath);

        var directory = Path.GetDirectoryName(fullDbPath);
        if (string.IsNullOrEmpty(directory))
            directory = Path.Combine(AppContext.BaseDirectory, "data");

        Directory.CreateDirectory(directory);
        _storePath = Path.Combine(directory, "platform_serial.txt");
        _current = Load();
        if (_current < MinSerial)
            _current = MinSerial;
    }

    public static ushort Next()
    {
        lock (Lock)
        {
            _current = _current >= ushort.MaxValue ? MinSerial : _current + 1;
            Save();
            return (ushort)_current;
        }
    }

    private static int Load()
    {
        try
        {
            if (!File.Exists(_storePath))
                return MinSerial;

            var text = File.ReadAllText(_storePath).Trim();
            if (int.TryParse(text, out var value) && value > 0)
                return value;
        }
        catch
        {
        }

        return MinSerial;
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(_storePath, _current.ToString());
        }
        catch
        {
        }
    }
}
