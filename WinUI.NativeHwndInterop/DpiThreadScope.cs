using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;

namespace WinUI.NativeHwndInterop;

/// <summary>Optional DPI awareness override when creating native content.</summary>
public enum NativeDpiAwareness
{
    /// <summary>Leave the calling thread's DPI context unchanged.</summary>
    Inherit,
    Unaware,
    SystemAware,
    PerMonitorV2,
}

/// <summary>Controls whether redirected backing pixels match native DPI scaling.</summary>
public enum HwndDpiScalingMode
{
    /// <summary>No compensation; preserves the original surface behavior.</summary>
    None,
    /// <summary>Compensate for the difference between parent and child window DPI.</summary>
    MatchNativeWindow,
}

/// <summary>Temporarily overrides DPI settings on the calling thread.</summary>
/// <remarks>Dispose on the creating thread in reverse creation order. Do not span an await,
/// dispatch messages, or perform unrelated XAML work inside a scope. MixedHosting must surround
/// parent HWND creation; it does not change existing HWNDs. No process-wide setting is changed.</remarks>
public sealed class DpiThreadScope : IDisposable
{
    [ThreadStatic]
    private static DpiThreadScope? s_current;
    private readonly uint _threadId = PInvoke.GetCurrentThreadId();
    private readonly DpiThreadScope? _previous = s_current;
    private DPI_HOSTING_BEHAVIOR? _hosting;
    private DPI_AWARENESS_CONTEXT? _awareness;
    private bool _disposed;

    private DpiThreadScope() { }

    /// <summary>Enable mixed-DPI hosting for parent windows created inside this scope.</summary>
    public static DpiThreadScope MixedHosting()
    {
        var scope = new DpiThreadScope();
        var previous = PInvoke.SetThreadDpiHostingBehavior(DPI_HOSTING_BEHAVIOR.DPI_HOSTING_BEHAVIOR_MIXED);
        if (previous == DPI_HOSTING_BEHAVIOR.DPI_HOSTING_BEHAVIOR_INVALID)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not enable mixed DPI hosting.");
        }
        scope._hosting = previous;
        s_current = scope;
        return scope;
    }

    /// <summary>Override DPI awareness for native windows created inside this scope.</summary>
    public static DpiThreadScope Awareness(NativeDpiAwareness awareness)
    {
        DPI_AWARENESS_CONTEXT context = GetContext(awareness);
        return EnterAwareness(context);
    }

    /// <summary>Use the exact awareness context of a live window for DPI-virtualized native queries.</summary>
    public static DpiThreadScope AwarenessForWindow(nint windowHandle)
    {
        var context = PInvoke.GetWindowDpiAwarenessContext(new HWND(windowHandle));
        if (context == default)
        {
            throw new ArgumentException("A live HWND is required.", nameof(windowHandle));
        }
        return EnterAwareness(context);
    }

    private static DpiThreadScope EnterAwareness(DPI_AWARENESS_CONTEXT context)
    {
        var scope = new DpiThreadScope();
        if (context != default)
        {
            var previous = PInvoke.SetThreadDpiAwarenessContext(context);
            if (previous == default)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not change thread DPI awareness.");
            }
            scope._awareness = previous;
        }
        s_current = scope;
        return scope;
    }

    internal static DPI_AWARENESS_CONTEXT GetContext(NativeDpiAwareness awareness) => awareness switch
    {
        NativeDpiAwareness.Inherit => default,
        NativeDpiAwareness.Unaware => DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_UNAWARE,
        NativeDpiAwareness.SystemAware => DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_SYSTEM_AWARE,
        NativeDpiAwareness.PerMonitorV2 => DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
        _ => throw new ArgumentOutOfRangeException(nameof(awareness)),
    };

    public void Dispose()
    {
        if (PInvoke.GetCurrentThreadId() != _threadId)
        {
            throw new InvalidOperationException("DPI scopes must be disposed on their creating thread.");
        }
        if (_disposed) return;
        if (!ReferenceEquals(s_current, this))
        {
            throw new InvalidOperationException("DPI scopes must be disposed in reverse creation order.");
        }
        if (_awareness is { } awareness && PInvoke.SetThreadDpiAwarenessContext(awareness) == default)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not restore thread DPI awareness.");
        }
        if (_hosting is { } hosting && PInvoke.SetThreadDpiHostingBehavior(hosting) == DPI_HOSTING_BEHAVIOR.DPI_HOSTING_BEHAVIOR_INVALID)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not restore thread DPI hosting behavior.");
        }
        s_current = _previous;
        _disposed = true;
    }
}
