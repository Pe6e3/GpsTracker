namespace GpsTcpProxy.Models;

public sealed class TheftDetectionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Один телефон (устаревшее поле, для обратной совместимости).</summary>
    public string PhoneDeviceId { get; set; } = "phone";

    /// <summary>Список телефонов OwnTracks для сравнения и команд wakeup.</summary>
    public List<string> PhoneDeviceIds { get; set; } = [];

    public IReadOnlyList<string> GetPhoneDeviceIds()
    {
        var ids = new List<string>();

        if (!string.IsNullOrWhiteSpace(PhoneDeviceId))
            AddUnique(ids, PhoneDeviceId);

        if (PhoneDeviceIds != null)
        {
            foreach (var phoneId in PhoneDeviceIds)
                AddUnique(ids, phoneId);
        }

        return ids;
    }

    public bool IsPhoneDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        var normalizedId = DeviceRegistry.NormalizeId(deviceId);
        if (string.IsNullOrEmpty(normalizedId))
            normalizedId = deviceId.Trim();

        foreach (var phoneId in GetPhoneDeviceIds())
        {
            if (phoneId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                return true;

            var normalizedPhoneId = DeviceRegistry.NormalizeId(phoneId);
            if (!string.IsNullOrEmpty(normalizedPhoneId) &&
                normalizedPhoneId.Equals(normalizedId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void AddUnique(List<string> ids, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        var trimmed = deviceId.Trim();
        if (ids.Any(id => id.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return;

        ids.Add(trimmed);
    }

    /// <summary>Базовый допустимый разрыв между трекером и телефоном, км.</summary>
    public double BaseDistanceKm { get; set; } = 0.5;

    /// <summary>Максимальный разрыв при совместном движении (оба &gt; порога скорости), км.</summary>
    public double TogetherMovingMaxKm { get; set; } = 1.0;

    /// <summary>При расстоянии больше этого — тревога сразу, без накопления, км.</summary>
    public double CriticalDistanceKm { get; set; } = 2.0;

    /// <summary>Строгий порог, когда оба устройства почти стоят, км.</summary>
    public double StationaryMaxKm { get; set; } = 0.35;

    /// <summary>Скорость, выше которой считаем устройство «в движении», км/ч.</summary>
    public double MovingSpeedThresholdKmh { get; set; } = 15;

    /// <summary>Дополнительный допуск за каждую минуту устаревания координат телефона, км.</summary>
    public double GpsLagTolerancePerMinuteKm { get; set; } = 0.05;

    /// <summary>Не сравнивать, если координаты телефона старше этого, мин.</summary>
    public int MaxPhoneAgeMinutes { get; set; } = 30;

    /// <summary>Сколько подряд подозрительных точек нужно для тревоги.</summary>
    public int SuspiciousCountBeforeAlert { get; set; } = 2;

    /// <summary>Минимальный интервал между тревогами по одному устройству, мин.</summary>
    public int AlertCooldownMinutes { get; set; } = 30;

    /// <summary>Не тревожить после совместного выезда из геозоны, мин.</summary>
    public int DepartureGraceMinutes { get; set; } = 20;

    /// <summary>Окно для определения недавнего перемещения трекера, мин.</summary>
    public int RecentMovementWindowMinutes { get; set; } = 6;

    /// <summary>Если трекер сместился дальше этого за окно — считаем поездкой, м.</summary>
    public double RecentMovementDistanceMeters { get; set; } = 250;

    /// <summary>Строгий порог StationaryMaxKm только если телефон обновлялся недавно, мин.</summary>
    public int MinPhoneFreshnessForStationaryCapMinutes { get; set; } = 3;

    /// <summary>Дополнительный допуск за каждую минуту отставания телефона при поездке, км.</summary>
    public double MovingLagTolerancePerMinuteKm { get; set; } = 0.15;

    /// <summary>Максимальный допуск при поездке с отстающим телефоном, км.</summary>
    public double MovingLagMaxKm { get; set; } = 2.0;
}
