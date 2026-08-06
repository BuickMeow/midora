namespace Midora.Domain;

public sealed class ProjectEditingTimeSession : IDisposable
{
    private const ulong TimeSpanTicksPerMillisecond = TimeSpan.TicksPerMillisecond;

    private readonly object _sync = new();
    private readonly ProjectMetadata _metadata;
    private readonly TimeProvider _timeProvider;
    private readonly long _baseMilliseconds;
    private UInt128 _elapsedTimeSpanTicks;
    private long _lastTimestamp;
    private PauseReason _pauseReasons;
    private bool _disposed;

    public ProjectEditingTimeSession(
        MidoraProject project,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        _metadata = project.Metadata;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _baseMilliseconds = _metadata.TotalEditingTimeMilliseconds;
        _metadata.BeginEditingTimeSession();
        try
        {
            _lastTimestamp = _timeProvider.GetTimestamp();
        }
        catch
        {
            _metadata.EndEditingTimeSession();
            throw;
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _pauseReasons != PauseReason.None;
            }
        }
    }

    public long SnapshotTotalEditingTimeMilliseconds()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pauseReasons == PauseReason.None)
            {
                CaptureActiveElapsed();
            }
            return PublishSnapshot();
        }
    }

    public void NotifySystemSuspending() => AddPauseReason(PauseReason.SystemSuspended);

    public void NotifySystemResumed() => RemovePauseReason(PauseReason.SystemSuspended);

    public void BeginClosing() => AddPauseReason(PauseReason.Closing);

    public void CancelClosing() => RemovePauseReason(PauseReason.Closing);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                if (_pauseReasons == PauseReason.None)
                {
                    CaptureActiveElapsed();
                }
                _ = PublishSnapshot();
            }
            finally
            {
                _disposed = true;
                _metadata.EndEditingTimeSession();
            }
        }
    }

    private void AddPauseReason(PauseReason reason)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((_pauseReasons & reason) != 0)
            {
                return;
            }
            if (_pauseReasons == PauseReason.None)
            {
                CaptureActiveElapsed();
            }
            _pauseReasons |= reason;
            _ = PublishSnapshot();
        }
    }

    private void RemovePauseReason(PauseReason reason)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((_pauseReasons & reason) == 0)
            {
                return;
            }
            _pauseReasons &= ~reason;
            if (_pauseReasons == PauseReason.None)
            {
                _lastTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }

    private void CaptureActiveElapsed()
    {
        long now = _timeProvider.GetTimestamp();
        TimeSpan elapsed = _timeProvider.GetElapsedTime(_lastTimestamp, now);
        if (elapsed < TimeSpan.Zero)
        {
            throw new InvalidOperationException("The editing-time clock moved backwards.");
        }
        _lastTimestamp = now;
        UInt128 delta = (ulong)elapsed.Ticks;
        _elapsedTimeSpanTicks = UInt128.MaxValue - _elapsedTimeSpanTicks < delta
            ? UInt128.MaxValue
            : _elapsedTimeSpanTicks + delta;
    }

    private long PublishSnapshot()
    {
        UInt128 elapsedMilliseconds = _elapsedTimeSpanTicks / TimeSpanTicksPerMillisecond;
        ulong available = (ulong)(long.MaxValue - _baseMilliseconds);
        long total = elapsedMilliseconds > available
            ? long.MaxValue
            : _baseMilliseconds + (long)elapsedMilliseconds;
        _metadata.SetTotalEditingTimeMilliseconds(total);
        return total;
    }

    [Flags]
    private enum PauseReason
    {
        None = 0,
        SystemSuspended = 1,
        Closing = 2
    }
}
