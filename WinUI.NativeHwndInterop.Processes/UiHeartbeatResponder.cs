namespace WinUI.NativeHwndInterop.Processes;

/// <summary>Posts heartbeat acknowledgements through the UI dispatcher supplied by the caller.</summary>
/// <param name="tryEnqueue">A nonblocking UI post operation (not synchronous Invoke). Returns false when posting is rejected.</param>
/// <param name="acknowledge">Sends/queues a reply through caller-owned IPC; invoked only when the posted UI callback executes.</param>
public sealed class UiHeartbeatResponder(Func<Action, bool> tryEnqueue, Action<long> acknowledge)
{
    private readonly Func<Action, bool> _tryEnqueue = tryEnqueue ?? throw new ArgumentNullException(nameof(tryEnqueue));
    private readonly Action<long> _acknowledge = acknowledge ?? throw new ArgumentNullException(nameof(acknowledge));

    /// <summary>Queues a UI callback for a positive probe sequence. False means dispatcher rejection, not UI health.</summary>
    /// <remarks>The sender should allow only one outstanding probe. Exceptions from the reply delegate follow the UI dispatcher's policy.</remarks>
    public bool TryRespond(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        return _tryEnqueue(() => _acknowledge(sequence));
    }
}
