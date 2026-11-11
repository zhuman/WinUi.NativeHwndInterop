namespace WinUI.NativeHwndInterop.Processes;

/// <summary>Correlates UI probe/acknowledgement messages with a dedicated-thread watchdog. Owns no transport or dispatcher.</summary>
public sealed class UiHeartbeatMonitor : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly OffThreadWatchdog _watchdog;
    private long _sequence;
    private long _pending;
    private bool _disposed;

    /// <summary>Starts the acknowledgement deadline immediately. The timeout callback runs off-thread, never on a captured context.</summary>
    public UiHeartbeatMonitor(TimeSpan timeout, Action onTimeout) => _watchdog = new(timeout, onTimeout);

    public Task Completion => _watchdog.Completion;
    public bool HasExpired => _watchdog.HasExpired;

    /// <summary>Creates at most one outstanding probe. Schedule calls at your desired interval and send successful results through your IPC.</summary>
    /// <remarks>Sending probes does not extend the deadline. Returns false while a probe is outstanding, after expiration, or after disposal.</remarks>
    public bool TryCreateProbe(out long sequence)
    {
        lock (_gate)
        {
            sequence = 0;
            if (_disposed || _pending != 0 || _watchdog.HasExpired) return false;
            _pending = sequence = checked(++_sequence);
            return true;
        }
    }

    /// <summary>Accepts only the outstanding sequence; stale, duplicate, unsolicited, or late replies cannot extend the deadline.</summary>
    public bool Acknowledge(long sequence)
    {
        lock (_gate)
        {
            if (_disposed || sequence <= 0 || sequence != _pending || !_watchdog.Reset()) return false;
            _pending = 0;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watchdog.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }
}
