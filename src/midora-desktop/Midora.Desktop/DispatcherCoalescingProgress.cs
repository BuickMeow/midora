using System.Windows.Threading;

namespace Midora.Desktop;

internal sealed class DispatcherCoalescingProgress<T> : IProgress<T>, IDisposable
{
    private readonly object _gate = new();
    private readonly Dispatcher _dispatcher;
    private readonly Action<T> _callback;
    private readonly DispatcherTimer _timer;
    private T? _latest;
    private bool _hasLatest;
    private bool _disposed;

    public DispatcherCoalescingProgress(
        Dispatcher dispatcher,
        TimeSpan interval,
        Action<T> callback)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _dispatcher.VerifyAccess();
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = interval
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _latest = value;
            _hasLatest = true;
        }
    }

    public void Flush()
    {
        _dispatcher.VerifyAccess();
        if (TryTakeLatest(out T? value)) _callback(value!);
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        _timer.Stop();
        _timer.Tick -= OnTick;
        T? value;
        bool hasValue;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            value = _latest;
            hasValue = _hasLatest;
            _latest = default;
            _hasLatest = false;
        }
        if (hasValue) _callback(value!);
    }

    private void OnTick(object? sender, EventArgs e) => Flush();

    private bool TryTakeLatest(out T? value)
    {
        lock (_gate)
        {
            if (_disposed || !_hasLatest)
            {
                value = default;
                return false;
            }
            value = _latest;
            _latest = default;
            _hasLatest = false;
            return true;
        }
    }
}
