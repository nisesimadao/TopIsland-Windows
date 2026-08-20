namespace TopIsland.Services;

public sealed record FocusTimerSnapshot(TimeSpan Remaining, TimeSpan Duration, bool IsRunning, bool IsComplete)
{
    public string Display => Remaining.TotalHours >= 1
        ? Remaining.ToString(@"hh\:mm\:ss")
        : Remaining.ToString(@"mm\:ss");
}

public sealed class FocusTimerService
{
    private TimeSpan _duration = TimeSpan.FromMinutes(25);
    private TimeSpan _remaining = TimeSpan.FromMinutes(25);
    private DateTime _lastStartedAt;
    private bool _isRunning;

    public FocusTimerSnapshot Snapshot()
    {
        var remaining = _remaining;
        if (_isRunning)
        {
            remaining -= DateTime.UtcNow - _lastStartedAt;
            if (remaining <= TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
                _remaining = TimeSpan.Zero;
                _isRunning = false;
            }
        }

        return new FocusTimerSnapshot(remaining, _duration, _isRunning, remaining <= TimeSpan.Zero);
    }

    public void Start(TimeSpan duration)
    {
        _duration = duration <= TimeSpan.Zero ? TimeSpan.FromMinutes(25) : duration;
        _remaining = _duration;
        _lastStartedAt = DateTime.UtcNow;
        _isRunning = true;
    }

    public void Toggle()
    {
        if (_isRunning)
        {
            _remaining = Snapshot().Remaining;
            _isRunning = false;
            return;
        }

        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = _duration;
        }
        _lastStartedAt = DateTime.UtcNow;
        _isRunning = true;
    }

    public void Reset()
    {
        _isRunning = false;
        _remaining = _duration;
    }
}
