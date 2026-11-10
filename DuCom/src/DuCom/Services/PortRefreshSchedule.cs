namespace DuCom.Services;

/// <summary>Bounded trailing debounce, driver-settling passes, and a presence-only watchdog.</summary>
internal sealed class PortRefreshSchedule(DateTimeOffset now)
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan MaximumDebounce = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan SettlingInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan SettlingDuration = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(2);
    private DateTimeOffset _nextFallback = now + FallbackInterval;
    private DateTimeOffset? _firstNotification;
    private DateTimeOffset _lastNotification;
    private DateTimeOffset? _settleUntil;
    private DateTimeOffset? _nextSettling;

    internal void Notify(DateTimeOffset timestamp)
    {
        _firstNotification ??= timestamp;
        _lastNotification = timestamp;
        _settleUntil = timestamp + SettlingDuration;
    }

    internal bool ShouldRefresh(DateTimeOffset timestamp)
    {
        bool notificationDue = _firstNotification is { } first
            && (timestamp - _lastNotification >= Debounce || timestamp - first >= MaximumDebounce);
        bool settlingDue = _nextSettling is { } next && timestamp >= next && timestamp <= _settleUntil;
        if (!notificationDue && !settlingDue && timestamp < _nextFallback)
        {
            return false;
        }

        _firstNotification = null;
        _nextFallback = timestamp + FallbackInterval;
        _nextSettling = timestamp + SettlingInterval;
        if (_nextSettling > _settleUntil || _settleUntil is null)
        {
            _nextSettling = null;
        }
        return true;
    }
}
