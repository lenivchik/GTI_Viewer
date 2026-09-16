using System.Windows.Threading;

namespace FirebirdViewer.Services;

/// <summary>
/// The heartbeat behind real-time mode: calls back on a fixed interval until stopped.
/// Ticks are raised on the UI thread, so the callback may touch bound collections
/// directly; it is handed a token that is cancelled by <see cref="Stop"/> so a poll
/// already in flight can abandon its work.
/// </summary>
public interface ILiveDataService : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Time between ticks. Clamped to a sane floor; may be changed while running.</summary>
    TimeSpan Interval { get; set; }

    void Start(Func<CancellationToken, Task> onTick);
    void Stop();
}

public sealed class LiveDataService : ILiveDataService
{
    /// <summary>Anything faster hammers the server without the operator seeing a difference.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _timer;
    private Func<CancellationToken, Task>? _onTick;
    private CancellationTokenSource? _cts;

    /// <summary>Set while a tick is awaiting the database — keeps slow polls from piling up.</summary>
    private bool _ticking;

    public LiveDataService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += OnTimerTick;
    }

    public bool IsRunning => _timer.IsEnabled;

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value < MinInterval ? MinInterval : value;
    }

    public void Start(Func<CancellationToken, Task> onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        Stop();
        _onTick = onTick;
        _cts = new CancellationTokenSource();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _onTick = null;
        if (_cts is null) return;
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
        _cts.Dispose();
        _cts = null;
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        if (_ticking) return;                 // previous poll still running — skip this beat
        var callback = _onTick;
        var cts = _cts;
        if (callback is null || cts is null || cts.IsCancellationRequested) return;

        _ticking = true;
        try
        {
            await callback(cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Stop() was called mid-poll.
        }
        catch (Exception)
        {
            // The callback reports its own failures; this guard only keeps an unexpected
            // one from faulting the app through an async void handler.
        }
        finally
        {
            _ticking = false;
        }
    }

    public void Dispose()
    {
        _timer.Tick -= OnTimerTick;
        Stop();
    }
}
