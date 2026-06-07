namespace GpsTcpProxy.Models;

public sealed class TheftDetectionSettings
{
    public bool Enabled { get; set; } = true;
    public string PhoneDeviceId { get; set; } = "phone";

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
}
