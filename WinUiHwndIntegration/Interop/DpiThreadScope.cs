using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

namespace WinUiHwndIntegration.Interop;

internal sealed class DpiThreadScope : IDisposable
{
    private readonly DPI_HOSTING_BEHAVIOR? _hosting;
    private readonly DPI_AWARENESS_CONTEXT? _awareness;
    private bool _disposed;

    private DpiThreadScope(DPI_HOSTING_BEHAVIOR? hosting, DPI_AWARENESS_CONTEXT? awareness)
    {
        _hosting = hosting;
        _awareness = awareness;
    }

    public static DpiThreadScope MixedHosting()
    {
        var previous = PInvoke.SetThreadDpiHostingBehavior(DPI_HOSTING_BEHAVIOR.DPI_HOSTING_BEHAVIOR_MIXED);
        if (previous == DPI_HOSTING_BEHAVIOR.DPI_HOSTING_BEHAVIOR_INVALID)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not enable mixed DPI hosting.");
        }
        return new(previous, null);
    }

    public static DpiThreadScope Awareness(DPI_AWARENESS_CONTEXT context)
    {
        var previous = PInvoke.SetThreadDpiAwarenessContext(context);
        if (previous == default)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not change thread DPI awareness.");
        }
        return new(null, previous);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_awareness is { } awareness && PInvoke.SetThreadDpiAwarenessContext(awareness) == default)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not restore thread DPI awareness.");
        }
        if (_hosting is { } hosting && PInvoke.SetThreadDpiHostingBehavior(hosting) == DPI_HOSTING_BEHAVIOR.DPI_HOSTING_BEHAVIOR_INVALID)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not restore thread DPI hosting behavior.");
        }
    }
}
