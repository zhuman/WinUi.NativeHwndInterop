using System.Diagnostics;

namespace WinUI.NativeHwndInterop.Processes;

/// <summary>A resettable, one-shot monotonic deadline serviced by a dedicated background thread, not a UI or thread-pool timer.</summary>
public sealed class OffThreadWatchdog : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Action _onTimeout;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TimeSpan _timeout;
    private long _resetAt = Stopwatch.GetTimestamp();
    private bool _disposed;
    private bool _expired;

    /// <summary>Arms the watchdog immediately. The callback runs once on its dedicated thread and must not require a UI dispatcher.</summary>
    public OffThreadWatchdog(TimeSpan timeout, Action onTimeout)
    {
        ValidateTimeout(timeout);
        ArgumentNullException.ThrowIfNull(onTimeout);
        _timeout = timeout;
        _onTimeout = onTimeout;
        new Thread(Run) { IsBackground = true, Name = "Plugin watchdog" }.Start();
    }

    /// <summary>Completes when disarmed or after the timeout callback returns. Faults if that callback throws; always observe this task.</summary>
    public Task Completion => _completion.Task;

    public bool HasExpired { get { lock (_gate) return _expired; } }

    /// <summary>Restarts the deadline unless disposed or expiration has already been committed.</summary>
    public bool Reset()
    {
        lock (_gate) return ResetCore(_timeout);
    }

    /// <summary>Restarts the deadline with a new timeout. Cannot resurrect an expired watchdog.</summary>
    public bool Reset(TimeSpan timeout)
    {
        ValidateTimeout(timeout);
        lock (_gate) return ResetCore(timeout);
    }

    private bool ResetCore(TimeSpan timeout)
    {
        if (_disposed || _expired) return false;
        // A delayed acknowledgement must not resurrect an already missed deadline.
        if (Stopwatch.GetElapsedTime(_resetAt) >= _timeout)
        {
            Monitor.PulseAll(_gate);
            return false;
        }
        _timeout = timeout;
        _resetAt = Stopwatch.GetTimestamp();
        Monitor.PulseAll(_gate);
        return true;
    }

    private void Run()
    {
        try
        {
            lock (_gate)
            {
                while (!_disposed)
                {
                    TimeSpan remaining = _timeout - Stopwatch.GetElapsedTime(_resetAt);
                    if (remaining <= TimeSpan.Zero) { _expired = true; break; }
                    Monitor.Wait(_gate, (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, int.MaxValue));
                }
                if (_disposed) return;
            }
            _onTimeout();
        }
        catch (Exception error)
        {
            _completion.TrySetException(error);
        }
        finally { _completion.TrySetResult(); }
    }

    internal static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "A finite positive timeout is required.");
    }

    /// <summary>Disarms without waiting. An already committed callback may still run; use DisposeAsync to await its completion.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Disarms and waits asynchronously for the dedicated thread/callback to finish. Do not call from the callback itself.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }
}
