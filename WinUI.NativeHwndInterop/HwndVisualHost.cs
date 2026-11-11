using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinUI.NativeHwndInterop;

/// <summary>
/// Positions a redirected surface and its real HWND for a loaded XAML host.
/// </summary>
/// <remarks>
/// All calls and events are UI-thread-affine. Tracking observes XAML layout, rectangular clips,
/// scroll events and UI-thread TransformToVisual snapshots. Arbitrary composition animations, transforms,
/// effects and off-thread manipulation cannot all be observed; use AlwaysCloaked when native input cannot
/// be guaranteed to align. Polling stops on failure and reports Error (or throws if no handler is attached).
/// Dispose and recreate the backend to resume tracking after a failure.
/// </remarks>
internal sealed unsafe class HwndVisualHost : IDisposable
{
    private const long SettleMilliseconds = 150;
    private const long ScrollTimeoutMilliseconds = 500;
    private readonly FrameworkElement _hostElement;
    private readonly XamlRoot _root;
    private readonly HWND _hostedHwnd;
    private readonly uint _threadId;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly List<ScrollViewer> _scrollViewers = new();
    private HwndPlacement _applied = new(default, default, 1, "not placed");
    private bool _hasApplied;
    private bool _hasMotion;
    private long _lastMotionTick;
    private bool _scrollActive;
    private long _lastScrollTick;
    private bool _renderingHooked;
    private bool _attached;
    private bool _disposed;
    private bool _faulted;
    private bool? _cloaked;
    private PixelRect? _appliedRegion;
    private bool _regionApplied;
    private HwndCloakMode _cloakMode;
    private HwndClipMode _clipMode;
    private HwndDpiScalingMode _dpiScalingMode;
    private double _appliedNativeScale;

    public HwndVisualHost(FrameworkElement hostElement, nint hostedHwnd, HwndDpiScalingMode dpiScalingMode)
    {
        ArgumentNullException.ThrowIfNull(hostElement);
        if (!Enum.IsDefined(dpiScalingMode)) throw new ArgumentOutOfRangeException(nameof(dpiScalingMode));
        _dpiScalingMode = dpiScalingMode;
        _hostElement = hostElement;
        _threadId = PInvoke.GetCurrentThreadId();
        if (!hostElement.DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("The host must belong to the calling thread.");
        }
        _root = hostElement.XamlRoot ?? throw new InvalidOperationException("The host must be attached to a XamlRoot.");
        _hostedHwnd = new HWND(hostedHwnd);
        _pollTimer = hostElement.DispatcherQueue.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(100);
        _pollTimer.IsRepeating = true;
        try
        {
            Surface = new NativeHwndSurface(hostedHwnd, ElementCompositionPreview.GetElementVisual(hostElement).Compositor);
            ElementCompositionPreview.SetElementChildVisual(hostElement, Surface.PlacementVisual);
            _attached = true;
            hostElement.SizeChanged += OnHostSizeChanged;
            hostElement.LayoutUpdated += OnHostLayoutUpdated;
            _root.Changed += OnXamlRootChanged;
            for (DependencyObject? current = VisualTreeHelper.GetParent(hostElement); current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is ScrollViewer scroll)
                {
                    _scrollViewers.Add(scroll);
                    scroll.ViewChanging += OnScrollViewChanging;
                    scroll.ViewChanged += OnScrollViewChanged;
                }
            }
            _pollTimer.Tick += OnPollTick;
            Update();
            _pollTimer.Start();
        }
        catch (Exception creationError)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Host creation and cleanup failed.", creationError, cleanupError);
            }
            throw;
        }
    }

    public NativeHwndSurface Surface { get; } = null!;
    public HwndPlacement Placement => _applied;
    public event EventHandler? StateChanged;
    public event EventHandler<NativeHwndErrorEventArgs>? Error;
    public bool IsCloaked => _cloaked == true;
    public string CloakReason { get; private set; } = "";
    public int LastCloakResult { get; private set; }
    public int LastRegionError { get; private set; }

    public HwndCloakMode CloakMode
    {
        get => _cloakMode;
        set
        {
            VerifyAccess();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _cloakMode = value;
            Update();
        }
    }

    public HwndClipMode ClipMode
    {
        get => _clipMode;
        set
        {
            VerifyAccess();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _clipMode = value;
            Update();
        }
    }

    public HwndDpiScalingMode DpiScalingMode
    {
        get => _dpiScalingMode;
        set
        {
            VerifyAccess();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _dpiScalingMode = value;
            UpdateFromEvent();
        }
    }

    public string DescribeWindowRegion()
    {
        VerifyAccess();
        RECT box;
        GDI_REGION_TYPE type = PInvoke.GetWindowRgnBox(_hostedHwnd, &box);
        return type switch
        {
            GDI_REGION_TYPE.RGN_ERROR => "no region (or GetWindowRgnBox failed)",
            GDI_REGION_TYPE.NULLREGION => "empty",
            _ => $"{type} box {box.left},{box.top} {box.Width}x{box.Height}",
        };
    }

    private void VerifyAccess()
    {
        if (PInvoke.GetCurrentThreadId() != _threadId)
        {
            throw new InvalidOperationException("HwndVisualHost is thread-affine.");
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs args) => UpdateFromEvent();
    private void OnHostLayoutUpdated(object? sender, object args) => UpdateFromEvent();
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateFromEvent();
    private void OnPollTick(DispatcherQueueTimer sender, object args) => UpdateFromEvent();
    private void OnRendering(object? sender, object args) => UpdateFromEvent();

    private void OnScrollViewChanging(object? sender, ScrollViewerViewChangingEventArgs args)
    {
        _scrollActive = true;
        _lastScrollTick = Environment.TickCount64;
        UpdateFromEvent();
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        _scrollActive = args.IsIntermediate;
        _lastScrollTick = Environment.TickCount64;
        UpdateFromEvent();
    }

    private void UpdateFromEvent()
    {
        if (_disposed || _faulted) return;
        try
        {
            Update();
        }
        catch (Exception exception)
        {
            _faulted = true;
            _pollTimer.Stop();
            SetRenderingTracking(false);
            if (Error is { } handler)
            {
                handler(this, new NativeHwndErrorEventArgs(exception));
            }
            else
            {
                throw;
            }
        }
    }

    private void Update()
    {
        VerifyAccess();
        if (_faulted)
        {
            throw new InvalidOperationException("Placement tracking failed; dispose and recreate the host.");
        }
        HwndPlacement placement = ComputePlacement();
        long now = Environment.TickCount64;
        bool changed = !_hasApplied || placement != _applied;
        if (changed)
        {
            if (_hasApplied)
            {
                _hasMotion = true;
                _lastMotionTick = now;
            }
            if (!_hasApplied || placement.Bounds != _applied.Bounds || placement.Scale != _applied.Scale)
            {
                ApplyBounds(placement);
            }
            _applied = placement;
            _hasApplied = true;
        }
        // DPI can change without any XAML geometry change. Query after native placement so the
        // HWND has reached the intended monitor before compensating its backing pixels.
        double nativeScale = _dpiScalingMode == HwndDpiScalingMode.MatchNativeWindow ? Surface.NativeScaleFactor : 1;
        if (changed || nativeScale != _appliedNativeScale)
        {
            ApplyVisualBounds(placement, nativeScale);
            _appliedNativeScale = nativeScale;
            changed = true;
        }
        if (_scrollActive && now - _lastScrollTick > ScrollTimeoutMilliseconds)
        {
            _scrollActive = false;
        }
        bool moving = _scrollActive || (_hasMotion && now - _lastMotionTick < SettleMilliseconds);
        string reason = _cloakMode switch
        {
            HwndCloakMode.AlwaysCloaked => "mode: always",
            HwndCloakMode.NeverCloaked => "",
            _ when placement.UnrepresentableReason is not null => placement.UnrepresentableReason,
            _ when _scrollActive => "scrolling",
            _ when moving => "moving",
            _ when placement.Visible != placement.Bounds => "partially clipped",
            _ => "",
        };
        changed |= ApplyCloak(reason.Length != 0, reason);
        changed |= ApplyRegion(placement);
        SetRenderingTracking(moving);
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetRenderingTracking(bool enabled)
    {
        if (enabled == _renderingHooked) return;
        if (enabled) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = enabled;
    }

    private HwndPlacement ComputePlacement()
    {
        if (_hostElement.XamlRoot != _root)
        {
            throw new InvalidOperationException("The host moved to a different XamlRoot; recreate its backend.");
        }
        double scale = _root.RasterizationScale;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new InvalidOperationException("Invalid XamlRoot rasterization scale.");
        }
        double width = _hostElement.ActualWidth;
        double height = _hostElement.ActualHeight;
        if (!_hostElement.IsLoaded || width <= 0 || height <= 0)
        {
            return new(default, default, scale, "not loaded or empty");
        }
        GeneralTransform transform = _hostElement.TransformToVisual(null);
        Point origin = transform.TransformPoint(new Point(0, 0));
        Point right = transform.TransformPoint(new Point(width, 0));
        Point bottom = transform.TransformPoint(new Point(0, height));
        const double epsilon = 0.5;
        string? reason = null;
        if (Math.Abs(right.Y - origin.Y) > epsilon || Math.Abs(bottom.X - origin.X) > epsilon)
        {
            reason = "rotated/skewed";
        }
        else if (Math.Abs(right.X - origin.X - width) > epsilon || Math.Abs(bottom.Y - origin.Y - height) > epsilon)
        {
            reason = "scaled";
        }
        Rect visible = Intersect(new Rect(0, 0, _root.Size.Width, _root.Size.Height), new Rect(origin.X, origin.Y, width, height));
        for (DependencyObject? current = _hostElement; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not UIElement element) continue;
            if (element.Visibility != Visibility.Visible)
            {
                return new(default, default, scale, "not visible");
            }
            if (element.Opacity < 1) reason ??= "opacity < 1";
            if (element.Projection is not null) reason ??= "projection";
            if (element.Clip is { } geometry)
            {
                Rect clip = geometry.Rect;
                if (geometry.Transform is { } clipTransform) clip = clipTransform.TransformBounds(clip);
                visible = Intersect(visible, element.TransformToVisual(null).TransformBounds(clip));
            }
            if (current is FrameworkElement ancestor && current is (ScrollContentPresenter or ScrollPresenter))
            {
                visible = Intersect(visible, element.TransformToVisual(null).TransformBounds(new Rect(0, 0, ancestor.ActualWidth, ancestor.ActualHeight)));
            }
        }
        PixelRect bounds = new(ToPixel(origin.X * scale), ToPixel(origin.Y * scale),
            Math.Max(0, ToPixel(width * scale)), Math.Max(0, ToPixel(height * scale)));
        PixelRect visiblePixels = default;
        if (visible.Width > 0 && visible.Height > 0)
        {
            int left = Math.Max(bounds.Left, ToPixel(visible.Left * scale));
            int top = Math.Max(bounds.Top, ToPixel(visible.Top * scale));
            int rightEdge = Math.Min(bounds.Right, ToPixel(visible.Right * scale));
            int bottomEdge = Math.Min(bounds.Bottom, ToPixel(visible.Bottom * scale));
            visiblePixels = new(left, top, Math.Max(0, checked(rightEdge - left)), Math.Max(0, checked(bottomEdge - top)));
        }
        return new(bounds, visiblePixels, scale, reason);
    }

    private static int ToPixel(double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("Non-finite host geometry.");
        return checked((int)Math.Round(value));
    }

    private static Rect Intersect(Rect first, Rect second)
    {
        double left = Math.Max(first.Left, second.Left);
        double top = Math.Max(first.Top, second.Top);
        double right = Math.Min(first.Right, second.Right);
        double bottom = Math.Min(first.Bottom, second.Bottom);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void ApplyBounds(HwndPlacement placement)
    {
        PixelRect bounds = placement.Bounds;
        // Keep native input above the XAML island HWND; covered framework HWNDs can also stop rendering.
        if (!PInvoke.SetWindowPos(_hostedHwnd, HWND.Null, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetWindowPos failed.");
        }
        }

        private void ApplyVisualBounds(HwndPlacement placement, double nativeScale)
        {
            Visual visual = Surface.PlacementVisual;
            visual.Offset = Vector3.Zero;
            visual.Scale = new Vector3((float)(nativeScale / placement.Scale), (float)(nativeScale / placement.Scale), 1);
            visual.Size = new Vector2((float)(placement.Bounds.Width / nativeScale), (float)(placement.Bounds.Height / nativeScale));
        }

    private bool ApplyCloak(bool cloak, string reason)
    {
        bool changed = CloakReason != reason;
        if (_cloaked != cloak)
        {
            BOOL value = cloak;
            HRESULT result = PInvoke.DwmSetWindowAttribute(_hostedHwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
            LastCloakResult = result.Value;
            result.ThrowOnFailure();
            _cloaked = cloak;
            changed = true;
        }
        CloakReason = reason;
        return changed;
    }

    private bool ApplyRegion(HwndPlacement placement)
    {
        PixelRect bounds = placement.Bounds;
        PixelRect visible = placement.Visible;
        PixelRect? desired = _clipMode switch
        {
            HwndClipMode.VisibleArea when _cloaked == true || visible == bounds => null,
            HwndClipMode.VisibleArea when visible.Width <= 0 || visible.Height <= 0 => default(PixelRect),
            HwndClipMode.VisibleArea => new PixelRect(checked(visible.Left - bounds.Left), checked(visible.Top - bounds.Top), visible.Width, visible.Height),
            HwndClipMode.DebugLeftHalf => new PixelRect(0, 0, bounds.Width / 2, bounds.Height),
            _ => null,
        };
        if (_regionApplied && desired == _appliedRegion) return false;
        HRGN region = HRGN.Null;
        if (desired is PixelRect rectangle)
        {
            region = PInvoke.CreateRectRgn(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            if (region.IsNull)
            {
                LastRegionError = Marshal.GetLastPInvokeError();
                throw new Win32Exception(LastRegionError, "CreateRectRgn failed.");
            }
        }
        if (PInvoke.SetWindowRgn(_hostedHwnd, region, true) == 0)
        {
            LastRegionError = Marshal.GetLastPInvokeError();
            if (!region.IsNull && !PInvoke.DeleteObject(region))
            {
                int deleteError = Marshal.GetLastPInvokeError();
                throw new AggregateException(new Win32Exception(LastRegionError, "SetWindowRgn failed."),
                    new Win32Exception(deleteError, "DeleteObject failed."));
            }
            throw new Win32Exception(LastRegionError, "SetWindowRgn failed.");
        }
        LastRegionError = 0;
        _appliedRegion = desired;
        _regionApplied = true;
        return true;
    }

    public void Dispose()
    {
        if (PInvoke.GetCurrentThreadId() != _threadId)
        {
            throw new InvalidOperationException("HwndVisualHost must be disposed on its owning thread.");
        }
        if (_disposed) return;
        _disposed = true;
        List<Exception> errors = [];
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception exception) { errors.Add(exception); }
        }
        _hostElement.SizeChanged -= OnHostSizeChanged;
        _hostElement.LayoutUpdated -= OnHostLayoutUpdated;
        _root.Changed -= OnXamlRootChanged;
        foreach (ScrollViewer scroll in _scrollViewers)
        {
            scroll.ViewChanging -= OnScrollViewChanging;
            scroll.ViewChanged -= OnScrollViewChanged;
        }
        _scrollViewers.Clear();
        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTick;
        SetRenderingTracking(false);
        if (_attached)
        {
            Cleanup(() => ElementCompositionPreview.SetElementChildVisual(_hostElement, null));
            _attached = false;
        }
        if (PInvoke.IsWindow(_hostedHwnd))
        {
            if (_cloaked == true) Cleanup(() => ApplyCloak(false, ""));
            if (_regionApplied && _appliedRegion is not null)
            {
                Cleanup(() =>
                {
                    if (PInvoke.SetWindowRgn(_hostedHwnd, HRGN.Null, true) == 0)
                    {
                        LastRegionError = Marshal.GetLastPInvokeError();
                        throw new Win32Exception(LastRegionError, "Clearing the HWND region failed.");
                    }
                });
            }
        }
        Cleanup(() => Surface?.Dispose());
        if (errors.Count != 0) throw new AggregateException("HWND host cleanup failed.", errors);
    }
}
