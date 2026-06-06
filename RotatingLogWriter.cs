using System.Text;

namespace GpsTcpProxy;

public sealed class RotatingLogWriter
{
    public const string CurrentFileName = "current.log";
    private const string ArchiveDateFormat = "yyyy-MM-dd";

    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly object _lock = new();
    private DateTime _currentLocalDate;

    public RotatingLogWriter(string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_directory);
        _currentLocalDate = AppTime.NowLocal().Date;
    }

    public string DirectoryPath => _directory;

    public void Append(string text)
    {
        lock (_lock)
        {
            EnsureCurrentDay();
            File.AppendAllText(GetCurrentPath(), text, Encoding.UTF8);
        }
    }

    public void CheckRotation()
    {
        lock (_lock)
            EnsureCurrentDay();
    }

    public long GetDirectorySizeBytes()
    {
        if (!Directory.Exists(_directory))
            return 0;

        return Directory.EnumerateFiles(_directory, "*", SearchOption.TopDirectoryOnly)
            .Sum(path =>
            {
                try
                {
                    return new FileInfo(path).Length;
                }
                catch
                {
                    return 0L;
                }
            });
    }

    private void EnsureCurrentDay()
    {
        var today = AppTime.NowLocal().Date;
        if (today == _currentLocalDate)
            return;

        RotateDay(_currentLocalDate);
        _currentLocalDate = today;
        CleanupOldArchives();
    }

    private void RotateDay(DateTime day)
    {
        var currentPath = GetCurrentPath();
        if (!File.Exists(currentPath))
            return;

        var info = new FileInfo(currentPath);
        if (info.Length == 0)
            return;

        var archivePath = GetArchivePath(day);
        File.Copy(currentPath, archivePath, overwrite: true);
        File.WriteAllText(currentPath, string.Empty);
    }

    private void CleanupOldArchives()
    {
        if (_retentionDays <= 0)
            return;

        var threshold = AppTime.NowLocal().Date.AddDays(-_retentionDays);

        foreach (var path in Directory.EnumerateFiles(_directory, "*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == Path.GetFileNameWithoutExtension(CurrentFileName))
                continue;

            if (!DateTime.TryParseExact(name, ArchiveDateFormat, null, System.Globalization.DateTimeStyles.None, out var fileDate))
                continue;

            if (fileDate < threshold)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
        }
    }

    private string GetCurrentPath() => Path.Combine(_directory, CurrentFileName);

    private string GetArchivePath(DateTime day) =>
        Path.Combine(_directory, $"{day:yyyy-MM-dd}.log");
}
