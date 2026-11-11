using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinUI.NativeHwndInterop;

/// <summary>Hosts a same-thread child HWND as a XAML composition visual and a native input target.</summary>
/// <remarks>The factory transfers ownership of its INativeHwndContent to this control. Unloading destroys
/// the HWND; loading again calls the factory again. XAML overlays do not automatically intercept native input.</remarks>
public unsafe class NativeHwndHost : Control
{
    private INativeHwndContent? _content;
    private HwndVisualHost? _host;
    private bool _leaving;
    private bool _forward = true;
    private bool _recreating;
    private Func<nint, INativeHwndContent>? _factory;

    public NativeHwndHost()
    {
        IsTabStop = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        GettingFocus += OnGettingFocus;
        GotFocus += OnGotFocus;
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) => ApplyEnabled());
    }

    /// <summary>Creates a new WS_CHILD window directly parented to the supplied top-level HWND.</summary>
    public Func<nint, INativeHwndContent>? ChildWindowFactory
    {
        get => _factory;
        set
        {
            VerifyAccess();
            if (_factory != value)
            {
                _factory = value;
                RecreateContent();
            }
        }
    }

    public static readonly DependencyProperty NativeAlphaProperty = DependencyProperty.Register(
        nameof(NativeAlpha), typeof(int), typeof(NativeHwndHost), new PropertyMetadata(1, OnOptionsChanged));

    /// <summary>Native window alpha, from 0 to 255. Does not change the redirected visual's opacity.
    /// Zero also makes native mouse hit-testing transparent; one is the recommended embedding value.</summary>
    public int NativeAlpha
    {
        get => (int)GetValue(NativeAlphaProperty);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
            SetValue(NativeAlphaProperty, value);
        }
    }

    public static readonly DependencyProperty CloakModeProperty = DependencyProperty.Register(
        nameof(CloakMode), typeof(HwndCloakMode), typeof(NativeHwndHost),
        new PropertyMetadata(HwndCloakMode.NeverCloaked, OnOptionsChanged));

    public HwndCloakMode CloakMode
    {
        get => (HwndCloakMode)GetValue(CloakModeProperty);
        set => SetValue(CloakModeProperty, value);
    }

    public static readonly DependencyProperty ClipModeProperty = DependencyProperty.Register(
        nameof(ClipMode), typeof(HwndClipMode), typeof(NativeHwndHost),
        new PropertyMetadata(HwndClipMode.VisibleArea, OnOptionsChanged));

    public HwndClipMode ClipMode
    {
        get => (HwndClipMode)GetValue(ClipModeProperty);
        set => SetValue(ClipModeProperty, value);
    }

    public static readonly DependencyProperty ChildDpiAwarenessProperty = DependencyProperty.Register(
        nameof(ChildDpiAwareness), typeof(NativeDpiAwareness), typeof(NativeHwndHost),
        new PropertyMetadata(NativeDpiAwareness.Inherit, OnChildDpiAwarenessChanged));

    /// <summary>Optional thread DPI override around CreateContent only. Changing it recreates content.
    /// The caller must enable mixed hosting when creating the parent HWND for a different child context.</summary>
    public NativeDpiAwareness ChildDpiAwareness
    {
        get => (NativeDpiAwareness)GetValue(ChildDpiAwarenessProperty);
        set => SetValue(ChildDpiAwarenessProperty, value);
    }

    public static readonly DependencyProperty DpiScalingModeProperty = DependencyProperty.Register(
        nameof(DpiScalingMode), typeof(HwndDpiScalingMode), typeof(NativeHwndHost),
        new PropertyMetadata(HwndDpiScalingMode.None, OnOptionsChanged));

    /// <summary>Opt-in compensation matching the native parent/child DPI ratio. Independent of clipping
    /// and creation awareness; also works for HWNDs created by a caller-managed factory.</summary>
    public HwndDpiScalingMode DpiScalingMode
    {
        get => (HwndDpiScalingMode)GetValue(DpiScalingModeProperty);
        set => SetValue(DpiScalingModeProperty, value);
    }

    private static void OnChildDpiAwarenessChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (NativeHwndHost)sender;
        if (!Enum.IsDefined(control.ChildDpiAwareness))
        {
            throw new ArgumentOutOfRangeException(nameof(ChildDpiAwareness));
        }
        control.RecreateContent();
    }

    public nint WindowHandle => _content?.WindowHandle ?? 0;
    /// <summary>The control-owned surface, or null before content is loaded. Do not dispose or reparent it.</summary>
    public INativeHwndSurface? Surface => _host?.Surface;
    public HwndPlacement Placement => _host?.Placement ?? default;
    public bool IsCloaked => _host?.IsCloaked ?? false;
    public string CloakReason => _host?.CloakReason ?? "";
    public int LastCloakResult => _host?.LastCloakResult ?? 0;
    public int LastRegionError => _host?.LastRegionError ?? 0;
    public string DescribeWindowRegion() => _host?.DescribeWindowRegion() ?? "not attached";
    public Exception? LastError { get; private set; }
    public event EventHandler? StateChanged;
    public event EventHandler<NativeHwndErrorEventArgs>? HostingFailed;

    protected virtual INativeHwndContent? CreateContent(nint parentWindow) => _factory?.Invoke(parentWindow);

    protected void VerifyAccess()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("Native HWND hosts must be accessed on their WinUI UI thread.");
        }
    }

    protected void RecreateContent()
    {
        VerifyAccess();
        if (!IsLoaded || _recreating)
        {
            return;
        }
        _recreating = true;
        try
        {
            ReleaseContent();
            LastError = null;
            HWND parent = (HWND)Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var requestedContext = DpiThreadScope.GetContext(ChildDpiAwareness);
            if (ChildDpiAwareness != NativeDpiAwareness.Inherit &&
                !PInvoke.AreDpiAwarenessContextsEqual(requestedContext, PInvoke.GetWindowDpiAwarenessContext(parent)) &&
                PInvoke.GetWindowDpiHostingBehavior(parent) != Windows.Win32.UI.HiDpi.DPI_HOSTING_BEHAVIOR.DPI_HOSTING_BEHAVIOR_MIXED)
            {
                throw new InvalidOperationException("Create the parent HWND inside DpiThreadScope.MixedHosting() before requesting a different child DPI awareness.");
            }
            using (DpiThreadScope.Awareness(ChildDpiAwareness))
            {
                _content = CreateContent((nint)parent.Value);
            }
            if (_content is null)
            {
                return;
            }
            HWND child = (HWND)_content.WindowHandle;
            if (!PInvoke.IsWindow(child) || PInvoke.GetParent(child) != parent)
            {
                throw new ArgumentException("Hosted content must create a child HWND directly under the supplied parent.");
            }
            uint ownerThread = PInvoke.GetWindowThreadProcessId(child, null);
            if (ownerThread != PInvoke.GetCurrentThreadId())
            {
                throw new ArgumentException("Hosted content must belong to the WinUI UI thread.");
            }
            if (ChildDpiAwareness != NativeDpiAwareness.Inherit &&
                !PInvoke.AreDpiAwarenessContextsEqual(PInvoke.GetWindowDpiAwarenessContext(child), requestedContext))
            {
                throw new InvalidOperationException($"The content HWND did not preserve the requested {ChildDpiAwareness} DPI awareness.");
            }
            int style = PInvoke.GetWindowLong(child, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            if ((style & (int)WINDOW_STYLE.WS_CHILD) == 0)
            {
                throw new ArgumentException("The hosted window must have WS_CHILD style.");
            }
            int exStyle = PInvoke.GetWindowLong(child, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if ((exStyle & (int)WINDOW_EX_STYLE.WS_EX_LAYERED) == 0)
            {
                // A zero return can mean success with a previous style of zero. Clear the OS error,
                // not only .NET's cached P/Invoke error, before making that distinction.
                Marshal.SetLastSystemError(0);
                Marshal.SetLastPInvokeError(0);
                int previous = PInvoke.SetWindowLong(child, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
                    exStyle | (int)WINDOW_EX_STYLE.WS_EX_LAYERED);
                if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not enable layered window redirection.");
                }
            }
            ApplyAlpha();
            ApplyEnabled();
            _host = new HwndVisualHost(this, _content.WindowHandle, DpiScalingMode);
            _host.CloakMode = CloakMode;
            _host.ClipMode = ClipMode;
            _host.StateChanged += OnStateChanged;
            _host.Error += OnBackendError;
            _content.FocusNavigationRequested += OnFocusNavigationRequested;
            NativeKeyboardBridge.Register(this);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ReleaseContent();
            ReportFailure(ex);
        }
        finally
        {
            _recreating = false;
        }
    }

    private static void OnOptionsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (NativeHwndHost)sender;
        if ((int)control.NativeAlpha is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(NativeAlpha), "Alpha must be in the range 0-255.");
        }
        if (!Enum.IsDefined(control.CloakMode) || !Enum.IsDefined(control.ClipMode) || !Enum.IsDefined(control.DpiScalingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Unknown HWND hosting mode.");
        }
        control.ApplyAlpha();
        if (control._host is not null)
        {
            control._host.CloakMode = control.CloakMode;
            control._host.ClipMode = control.ClipMode;
            control._host.DpiScalingMode = control.DpiScalingMode;
        }
        control.StateChanged?.Invoke(control, EventArgs.Empty);
    }

    private void ApplyAlpha()
    {
        if (_content is not null && !PInvoke.SetLayeredWindowAttributes((HWND)_content.WindowHandle,
            default, (byte)NativeAlpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not set native HWND alpha.");
        }
    }

    private void ApplyEnabled()
    {
        if (_content is not null)
        {
            PInvoke.EnableWindow((HWND)_content.WindowHandle, IsEnabled);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => RecreateContent();
    private void OnUnloaded(object sender, RoutedEventArgs args) => ReleaseContent();
    private void OnStateChanged(object? sender, EventArgs args) => StateChanged?.Invoke(this, args);
    private void OnBackendError(object? sender, NativeHwndErrorEventArgs args) => ReportFailure(args.Exception);

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        _forward = args.Direction != FocusNavigationDirection.Previous;
    }

    private void OnGotFocus(object sender, RoutedEventArgs args)
    {
        if (!_leaving && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this) && _content is not null)
        {
            StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            if (!_content.Focus(_forward))
            {
                LeaveContent(_forward);
            }
        }
    }

    private void OnFocusNavigationRequested(object? sender, NativeFocusNavigationEventArgs args) =>
        args.Handled = LeaveContent(args.Forward);

    private bool LeaveContent(bool forward)
    {
        _leaving = true;
        try
        {
            if (!Focus(FocusState.Programmatic))
            {
                return false;
            }
            return FocusManager.TryMoveFocus(forward ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous,
                new FindNextElementOptions { SearchRoot = XamlRoot.Content });
        }
        finally
        {
            _leaving = false;
        }
    }

    internal bool OwnsWindow(HWND window) => _content is not null &&
        (window == (HWND)_content.WindowHandle || PInvoke.IsChild((HWND)_content.WindowHandle, window));

    internal bool Preprocess(ref NativeMessage message) => _content?.TranslateMessage(ref message) == true;

    protected internal void ReportFailure(Exception exception)
    {
        LastError = exception;
        System.Diagnostics.Trace.TraceError($"Native HWND hosting failed: {exception}");
        if (HostingFailed is { } handler)
        {
            handler(this, new NativeHwndErrorEventArgs(exception));
        }
        else
        {
            throw new InvalidOperationException("Native HWND hosting failed. Handle HostingFailed to display errors in the UI.", exception);
        }
    }

    private void ReleaseContent()
    {
        try
        {
            NativeKeyboardBridge.Unregister(this);
        }
        finally
        {
            try
            {
                HwndVisualHost? host = _host;
                _host = null;
                if (host is not null)
                {
                    host.StateChanged -= OnStateChanged;
                    host.Error -= OnBackendError;
                    host.Dispose();
                }
            }
            finally
            {
                INativeHwndContent? content = _content;
                _content = null;
                if (content is not null)
                {
                    content.FocusNavigationRequested -= OnFocusNavigationRequested;
                    content.Dispose();
                }
            }
        }
    }
}
