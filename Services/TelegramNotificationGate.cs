namespace GpsTcpProxy.Services;

public sealed class TelegramNotificationGate
{
    private readonly object _lock = new();
    private bool _permanentlyMuted;
    private DateTime? _muteUntilUtc;

    public event Action<string>? AutoResumed;

    public bool CanNotify
    {
        get
        {
            lock (_lock)
            {
                CheckExpiredMuteLocked(notify: false);
                if (_permanentlyMuted)
                    return false;

                return !_muteUntilUtc.HasValue || DateTime.UtcNow >= _muteUntilUtc.Value;
            }
        }
    }

    public string StatusText
    {
        get
        {
            lock (_lock)
            {
                CheckExpiredMuteLocked(notify: false);
                if (!_permanentlyMuted && (!_muteUntilUtc.HasValue || DateTime.UtcNow >= _muteUntilUtc.Value))
                    return "уведомления включены";

                if (_permanentlyMuted && !_muteUntilUtc.HasValue)
                    return "уведомления отключены (stop)";

                if (_muteUntilUtc.HasValue)
                {
                    var localUntil = AppTime.UtcToLocal(_muteUntilUtc.Value);
                    return $"уведомления отключены до {localUntil:dd.MM.yyyy HH:mm}";
                }

                return "уведомления отключены";
            }
        }
    }

    public void StopPermanent()
    {
        lock (_lock)
        {
            _permanentlyMuted = true;
            _muteUntilUtc = null;
        }
    }

    public void StopForHours(int hours)
    {
        if (hours <= 0)
        {
            StopPermanent();
            return;
        }

        lock (_lock)
        {
            _permanentlyMuted = false;
            _muteUntilUtc = DateTime.UtcNow.AddHours(hours);
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _permanentlyMuted = false;
            _muteUntilUtc = null;
        }
    }

    public void CheckExpiredMute()
    {
        lock (_lock)
            CheckExpiredMuteLocked(notify: true);
    }

    private void CheckExpiredMuteLocked(bool notify)
    {
        if (_permanentlyMuted || !_muteUntilUtc.HasValue)
            return;

        if (DateTime.UtcNow < _muteUntilUtc.Value)
            return;

        _muteUntilUtc = null;
        if (!notify)
            return;

        AutoResumed?.Invoke("🔔 Уведомления снова включены (время stopN истекло).");
    }
}
