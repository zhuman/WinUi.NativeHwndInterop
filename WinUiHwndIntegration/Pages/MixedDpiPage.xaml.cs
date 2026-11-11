using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI.NativeHwndInterop;
using WinUiHwndIntegration.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;

namespace WinUiHwndIntegration.Pages;

public sealed partial class MixedDpiPage : Page
{
    private readonly List<NativeHwndHost> _hosts = [];
    private readonly ComboBox _mode = new()
    {
        Header = "Rendering comparison",
        ItemsSource = new[] { "Redirected + input (alpha 1)", "Native only (alpha 255)", "Redirected only (cloaked)" },
        SelectedIndex = 0,
    };
    private readonly CheckBox _clip = new() { Content = "Apply visible-area input regions (known layered-window drawing bug)" };
    private readonly CheckBox _compensate = new() { Content = "Compensate redirected DPI scale (experimental)" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    public MixedDpiPage()
    {
        InitializeComponent();
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Mixed-mode DPI HWND experiment", FontSize = 24 });
        panel.Children.Add(new TextBlock
        {
            Text = "The WinUI parent is created with MIXED hosting; each child is created under its own thread DPI context, then the thread is restored. Change display scale in Settings while this page is open. Unaware stays at 96 DPI, system-aware at system DPI, and PMv2 follows the monitor. Compare native and redirected views using the white 96-DIP and orange XAML rulers. At 200%, the raw unaware visual is half-sized; enable experimental compensation to match native size and input. Compensation uses the opt-in core library API. Regions start disabled to isolate DPI behavior; offscreen HWNDs can still intercept input in this diagnostic mode.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_mode);
        panel.Children.Add(_clip);
        panel.Children.Add(_compensate);
        panel.Children.Add(_error);
        AddSample(panel, "Unaware", NativeDpiAwareness.Unaware);
        AddSample(panel, "System aware", NativeDpiAwareness.SystemAware);
        AddSample(panel, "PerMonitorV2", NativeDpiAwareness.PerMonitorV2);
        _mode.SelectionChanged += (_, _) => ApplyOptions();
        _clip.Click += (_, _) => ApplyOptions();
        _compensate.Click += (_, _) => ApplyOptions();
        Content = new ScrollViewer { Content = panel };
    }

    private void AddSample(StackPanel panel, string label, NativeDpiAwareness awareness)
    {
        panel.Children.Add(new TextBlock { Text = label, FontSize = 20 });
        var diagnostics = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var host = new NativeHwndHost
        {
            Width = 400,
            Height = 190,
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipMode = HwndClipMode.None,
            ChildDpiAwareness = awareness,
            ChildWindowFactory = parent =>
            {
                var child = new SampleChildWindow((HWND)parent, label);
                return new NativeWindowContent(child.NativeHandle, child.Dispose);
            },
        };
        host.HostingFailed += (_, args) => _error.Text = $"{label}: {args.Exception}";
        host.StateChanged += (_, _) =>
        {
            if (host.WindowHandle == 0) return;
            ApplyOptions(host);
            HWND hwnd = (HWND)host.WindowHandle;
            if (!PInvoke.GetWindowRect(hwnd, out RECT physical))
            {
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            }
            RECT logical;
            using (DpiThreadScope.AwarenessForWindow(host.WindowHandle))
            {
                if (!PInvoke.GetClientRect(hwnd, out logical))
                {
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
                }
            }
            bool restored = PInvoke.AreDpiAwarenessContextsEqual(PInvoke.GetThreadDpiAwarenessContext(), DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            diagnostics.Text = $"{label}: HWND 0x{host.WindowHandle:X}, DPI {PInvoke.GetDpiForWindow(hwnd)}, " +
                $"logical client {logical.Width}x{logical.Height}, physical window {physical.Width}x{physical.Height}; " +
                $"XAML scale {host.XamlRoot.RasterizationScale:F2}, expected {host.Placement.Bounds.Width}x{host.Placement.Bounds.Height}px; " +
                $"native scale factor {host.Surface!.NativeScaleFactor:F2}; " +
                $"UI thread PMv2 restored: {restored}, hosting: {PInvoke.GetThreadDpiHostingBehavior()}";
        };
        _hosts.Add(host);
        panel.Children.Add(diagnostics);
        panel.Children.Add(new TextBox { Header = $"Before {label}", PlaceholderText = "Tab into native button" });
        panel.Children.Add(host);
        panel.Children.Add(new Border
        {
            Width = 96, Height = 16, HorizontalAlignment = HorizontalAlignment.Left,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
            Child = new TextBlock { Text = "96 XAML DIPs", FontSize = 10, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) },
        });
        panel.Children.Add(new TextBox { Header = $"After {label}", PlaceholderText = "Shift+Tab into native button" });
    }

    private void ApplyOptions()
    {
        foreach (var host in _hosts) ApplyOptions(host);
    }

    private void ApplyOptions(NativeHwndHost host)
    {
        int alpha = _mode.SelectedIndex == 1 ? 255 : 1;
        var cloak = _mode.SelectedIndex == 2 ? HwndCloakMode.AlwaysCloaked : HwndCloakMode.NeverCloaked;
        var clip = _clip.IsChecked == true ? HwndClipMode.VisibleArea : HwndClipMode.None;
        if (host.NativeAlpha != alpha) host.NativeAlpha = alpha;
        if (host.CloakMode != cloak) host.CloakMode = cloak;
        if (host.ClipMode != clip) host.ClipMode = clip;
        var scaling = _compensate.IsChecked == true ? HwndDpiScalingMode.MatchNativeWindow : HwndDpiScalingMode.None;
        if (host.DpiScalingMode != scaling) host.DpiScalingMode = scaling;
        if (host.Surface is { } surface)
        {
            surface.PlacementVisual.Opacity = _mode.SelectedIndex == 1 ? 0 : 1;
        }
    }
}
