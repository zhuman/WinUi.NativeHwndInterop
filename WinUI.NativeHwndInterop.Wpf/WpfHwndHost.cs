using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WinUI.NativeHwndInterop.Wpf;

/// <summary>
/// Hosts a WPF <see cref="FrameworkElement"/> inside a WinUI layout through an opaque child <see cref="HwndSource"/>
/// inside a layered native container HWND.
/// </summary>
/// <remarks>
/// <para>The caller owns <see cref="Child"/>. The host never disposes it; it only detaches it from the
/// <see cref="HwndSource"/> when the host is unloaded, recreated or the child changes.</para>
/// <para>Supported: the <see cref="IKeyboardInputSink"/> sequence (<c>TranslateAccelerator</c>, <c>TranslateChar</c>, <c>OnMnemonic</c>)
/// is driven for keyboard messages inside the hosted window, so access keys and accelerators work, and Tab/Shift+Tab leaving the WPF
/// tab order is reported through <see cref="IKeyboardInputSite.OnNoMoreTabStops"/> and handed back to XAML.</para>
/// <para>Not supported: a running WPF <c>Application</c>/<c>Window</c> is not required nor provided (resources must be supplied by the element itself),
/// directional (arrow-key) focus hand-off to XAML, mouse-wheel chaining, <c>ComponentDispatcher</c> thread-level notifications,
/// and per-pixel transparency. Content must be created and used on the WinUI UI thread, which also becomes the WPF dispatcher thread.</para>
/// </remarks>
public class WpfHwndHost : NativeHwndHost
{
    private FrameworkElement? _child;

    /// <summary>Gets or sets the caller-owned WPF element. It must not be a Window or already have a parent or presentation source.</summary>
    /// <exception cref="InvalidOperationException">The host or element belongs to another UI thread, or the element is already parented or attached to a presentation source.</exception>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public FrameworkElement? Child
    {
        get
        {
            VerifyUiThread();
            return _child;
        }
        set
        {
            VerifyUiThread();
            if (ReferenceEquals(_child, value))
            {
                return;
            }

            if (value is not null)
            {
                ValidateChild(value);
            }

            _child = value;
            RecreateContent();
        }
    }

    /// <inheritdoc />
    protected override INativeHwndContent? CreateContent(nint parentWindow)
    {
        VerifyUiThread();
        if (_child is null)
        {
            return null;
        }

        ValidateChild(_child);
        return new WpfContent(parentWindow, _child, ReportFailure, DispatcherQueue);
    }

    private static void ValidateChild(FrameworkElement value)
    {
        if (!value.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("The element belongs to another UI thread.");
        }

        if (value is Window)
        {
            throw new ArgumentException("A WPF Window cannot be hosted. Supply its detached content instead.", nameof(value));
        }

        if (LogicalTreeHelper.GetParent(value) is not null ||
            System.Windows.Media.VisualTreeHelper.GetParent(value) is not null ||
            PresentationSource.FromVisual(value) is not null)
        {
            throw new InvalidOperationException("The element already has a parent or presentation source. Detach it first.");
        }
    }

    private void VerifyUiThread()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("WpfHwndHost must be accessed on its UI thread.");
        }
    }
}

internal sealed class WpfContent : INativeHwndContent, IKeyboardInputSite
{
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_DEADCHAR = 0x0103;
    private const uint WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105, WM_SYSCHAR = 0x0106, WM_SYSDEADCHAR = 0x0107;
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private static readonly SubclassProc ContainerSubclass = OnContainerMessage;
    private HwndSource? _source;
    private nint _container;
    private GCHandle _callbackRoot;
    private bool _inTranslate;
    private readonly Action<Exception> _reportError;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public WpfContent(nint parentWindow, FrameworkElement child, Action<Exception> reportError,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _reportError = reportError;
        _dispatcher = dispatcher;
        try
        {
            // HwndTarget removes WS_EX_LAYERED from opaque WPF windows. Layer only the native parent.
            _container = CreateWindowEx(0x00080000, "STATIC", "WinUI.NativeHwndInterop.Wpf.Container",
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                0, 0, 1, 1, parentWindow, 0, 0, 0);
            if (_container == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _callbackRoot = GCHandle.Alloc(this);
            if (!SetWindowSubclass(_container, ContainerSubclass, 1, (nuint)GCHandle.ToIntPtr(_callbackRoot)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not subclass the WPF container window.");
            }

            var parameters = new HwndSourceParameters("WinUI.NativeHwndInterop.Wpf")
            {
                ParentWindow = _container,
                WindowStyle = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                PositionX = 0,
                PositionY = 0,
                Width = 1,
                Height = 1,
                AcquireHwndFocusInMenuMode = false,
            };

            _source = new HwndSource(parameters);
            ((IKeyboardInputSink)_source).KeyboardInputSite = this;
            _source.RootVisual = child;
            ResizeSource();
        }
        catch (Exception constructionError)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("WPF hosting construction and cleanup failed.", constructionError, cleanupError);
            }

            throw;
        }
    }

    public nint WindowHandle => _container;

    public event EventHandler<NativeFocusNavigationEventArgs>? FocusNavigationRequested;

    public bool Focus(bool forward)
    {
        if (_source is null)
        {
            return false;
        }

        var request = new TraversalRequest(forward ? FocusNavigationDirection.First : FocusNavigationDirection.Last);
        return ((IKeyboardInputSink)_source).TabInto(request);
    }

    public bool TranslateMessage(ref NativeMessage message)
    {
        // A child HwndSource does not register with ComponentDispatcher, so drive the sink sequence here.
        if (_source is null || _inTranslate || message.Id < WM_KEYDOWN || message.Id > WM_SYSDEADCHAR)
        {
            return false;
        }

        var msg = new MSG
        {
            hwnd = message.Window,
            message = (int)message.Id,
            wParam = (nint)message.WParam,
            lParam = message.LParam,
            time = (int)message.Time,
            pt_x = message.X,
            pt_y = message.Y,
        };

        var sink = (IKeyboardInputSink)_source;
        var modifiers = GetModifiers();
        _inTranslate = true;
        try
        {
            switch (message.Id)
            {
                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYDOWN:
                case WM_SYSKEYUP:
                    return sink.TranslateAccelerator(ref msg, modifiers);
                case WM_CHAR:
                case WM_DEADCHAR:
                case WM_SYSDEADCHAR:
                    return sink.TranslateChar(ref msg, modifiers);
                case WM_SYSCHAR:
                    return sink.TranslateChar(ref msg, modifiers) || sink.OnMnemonic(ref msg, modifiers);
                default:
                    return false;
            }
        }
        finally
        {
            _inTranslate = false;
        }
    }

    public void Dispose()
    {
        List<Exception> errors = [];
        var source = _source;
        _source = null;
        if (source is not null && !source.IsDisposed)
        {
            try
            {
                ((IKeyboardInputSink)source).KeyboardInputSite = null;
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                source.RootVisual = null;
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            try
            {
                source.Dispose();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (_container != 0)
        {
            // WM_NCDESTROY removes the subclass. If destruction fails, retain any callback root.
            if (!DestroyWindow(_container))
            {
                errors.Add(new Win32Exception(Marshal.GetLastWin32Error(), "Could not destroy the WPF container window."));
            }
            else
            {
                _container = 0;
                ReleaseCallbackRoot();
            }
        }

        if (errors.Count != 0)
        {
            throw new AggregateException("WPF hosting cleanup failed.", errors);
        }
    }

    private void ResizeSource()
    {
        if (_source is not { IsDisposed: false } source)
        {
            return;
        }

        if (!GetClientRect(_container, out var rect))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the WPF container client area.");
        }

        if (!SetWindowPos(source.Handle, 0, 0, 0, Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top), 0x0014)) // SWP_NOZORDER | SWP_NOACTIVATE
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resize the WPF child window.");
        }
    }

    private static nint OnContainerMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        WpfContent? content = null;
        try
        {
            content = GCHandle.FromIntPtr((nint)data).Target as WpfContent;
            if (content is not null)
            {
                if (message == 0x0005) // WM_SIZE
                {
                    content.ResizeSource();
                }
                else if (message == 0x0082) // WM_NCDESTROY
                {
                    if (!RemoveWindowSubclass(hwnd, ContainerSubclass, id))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the WPF container subclass.");
                    }

                    content._container = 0;
                    content.ReleaseCallbackRoot();
                }
            }
        }
        catch (Exception error)
        {
            // Reporting is queued: HostingFailed handlers must never execute inside the native callback.
            TraceCallbackFailure($"WPF HWND callback failed: {error}");
            try
            {
                if (content is null || !content._dispatcher.TryEnqueue(() => content._reportError(error)))
                {
                    TraceCallbackFailure("Could not enqueue WPF HWND failure notification.");
                }
            }
            catch (Exception reportingError)
            {
                TraceCallbackFailure($"WPF HWND failure reporting failed: {reportingError}; original error: {error}");
            }
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static void TraceCallbackFailure(string message)
    {
        try
        {
            System.Diagnostics.Trace.TraceError(message);
        }
        catch (Exception listenerError)
        {
            // A custom Trace listener must not prevent the independently queued failure notification.
            OutputDebugString($"{message}; Trace listener failed: {listenerError}");
        }
    }

    private void ReleaseCallbackRoot()
    {
        if (_callbackRoot.IsAllocated)
        {
            _callbackRoot.Free();
        }
    }

    IKeyboardInputSink IKeyboardInputSite.Sink => _source!;

    void IKeyboardInputSite.Unregister()
    {
    }

    bool IKeyboardInputSite.OnNoMoreTabStops(TraversalRequest request)
    {
        bool forward;
        switch (request?.FocusNavigationDirection)
        {
            case FocusNavigationDirection.Next:
                forward = true;
                break;
            case FocusNavigationDirection.Previous:
                forward = false;
                break;
            default:
                return false;
        }

        var args = new NativeFocusNavigationEventArgs(forward);
        FocusNavigationRequested?.Invoke(this, args);
        return args.Handled;
    }

    private static ModifierKeys GetModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (IsDown(VK_SHIFT)) modifiers |= ModifierKeys.Shift;
        if (IsDown(VK_CONTROL)) modifiers |= ModifierKeys.Control;
        if (IsDown(VK_MENU)) modifiers |= ModifierKeys.Alt;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", EntryPoint = "OutputDebugStringW", CharSet = CharSet.Unicode)]
    private static extern void OutputDebugString(string message);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
