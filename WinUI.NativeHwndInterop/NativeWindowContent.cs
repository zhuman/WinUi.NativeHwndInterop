using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinUI.NativeHwndInterop;

/// <summary>Basic dialog-style tab navigation for an existing same-thread native child window.</summary>
/// <remarks>The optional release callback owns destruction. Without it this wrapper does not destroy the HWND.
/// Use the framework-specific adapters instead for WinForms or WPF keyboard preprocessing.</remarks>
public sealed unsafe class NativeWindowContent : INativeHwndContent
{
    private readonly Action? _release;
    private bool _disposed;

    public NativeWindowContent(nint windowHandle, Action? release = null)
    {
        if (windowHandle == 0 || !PInvoke.IsWindow((HWND)windowHandle))
        {
            throw new ArgumentException("A live window handle is required.", nameof(windowHandle));
        }
        WindowHandle = windowHandle;
        _release = release;
    }

    public nint WindowHandle { get; private set; }
    public event EventHandler<NativeFocusNavigationEventArgs>? FocusNavigationRequested;

    public bool Focus(bool forward)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<HWND> stops = GetTabStops();
        HWND target = stops.Count == 0 ? (HWND)WindowHandle : forward ? stops[0] : stops[^1];
        PInvoke.SetFocus(target);
        return PInvoke.GetFocus() == target;
    }

    public bool TranslateMessage(ref NativeMessage message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.Id != PInvoke.WM_KEYDOWN || message.WParam != 9)
        {
            return false;
        }
        MSG native = new()
        {
            hwnd = (HWND)message.Window,
            message = message.Id,
            wParam = (WPARAM)message.WParam,
            lParam = (LPARAM)message.LParam,
        };
        nint dialogCode = PInvoke.SendMessage((HWND)message.Window, PInvoke.WM_GETDLGCODE,
            (WPARAM)message.WParam, (LPARAM)(nint)(&native)).Value;
        if ((dialogCode & 0x0006) != 0)
        {
            return false;
        }
        bool forward = PInvoke.GetKeyState(0x10) >= 0;
        List<HWND> stops = GetTabStops();
        int current = stops.IndexOf(PInvoke.GetFocus());
        int nextIndex = current + (forward ? 1 : -1);
        if (current < 0 || nextIndex < 0 || nextIndex >= stops.Count)
        {
            var args = new NativeFocusNavigationEventArgs(forward);
            FocusNavigationRequested?.Invoke(this, args);
            return args.Handled;
        }
        HWND next = stops[nextIndex];
        PInvoke.SetFocus(next);
        return PInvoke.GetFocus() == next;
    }

    private List<HWND> GetTabStops()
    {
        List<HWND> stops = [];
        HWND next = PInvoke.GetNextDlgTabItem((HWND)WindowHandle, HWND.Null, false);
        while (!next.IsNull && !stops.Contains(next))
        {
            stops.Add(next);
            next = PInvoke.GetNextDlgTabItem((HWND)WindowHandle, next, false);
        }
        return stops;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        WindowHandle = 0;
        _release?.Invoke();
    }
}
