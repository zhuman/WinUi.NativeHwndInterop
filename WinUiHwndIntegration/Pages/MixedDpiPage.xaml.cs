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
            Text = "The WinUI parent is created with MIXED hosting; each child is created under its own thread DPI context, then the thread is restored. Change display scale in Settings while this page is open. Unaware stays at 96 DPI, system-aware at system DPI, and PMv2 follows the monitor. Compare native and redirected views using the white 96-DIP and orange XAML rulers. At 200%, the raw unaware visual is half-sized; enable experimental compensation to match native size and input. This correction is sample-only. Regions start disabled to isolate DPI behavior; offscreen HWNDs can still intercept input in this diagnostic mode.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_mode);
        panel.Children.Add(_clip);
        panel.Children.Add(_compensate);
        panel.Children.Add(_error);
        AddSample(panel, "Unaware", DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_UNAWARE);
        AddSample(panel, "System aware", DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
        AddSample(panel, "PerMonitorV2", DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _mode.SelectionChanged += (_, _) => ApplyOptions();
        _clip.Click += (_, _) => ApplyOptions();
        _compensate.Click += (_, _) => ApplyOptions();
        Content = new ScrollViewer { Content = panel };
    }

    private void AddSample(StackPanel panel, string label, DPI_AWARENESS_CONTEXT context)
    {
        panel.Children.Add(new TextBlock { Text = label, FontSize = 20 });
        var diagnostics = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var host = new NativeHwndHost
        {
            Width = 400,
            Height = 190,
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipMode = HwndClipMode.None,
            ChildWindowFactory = parent =>
            {
                if (PInvoke.GetWindowDpiHostingBehavior((HWND)parent) != DPI_HOSTING_BEHAVIOR.DPI_HOSTING_BEHAVIOR_MIXED)
                {
                    throw new InvalidOperationException("The top-level HWND was not created with MIXED DPI hosting.");
                }
                SampleChildWindow child;
                using (DpiThreadScope.Awareness(context))
                {
                    child = new SampleChildWindow((HWND)parent, label);
                }
                if (!PInvoke.AreDpiAwarenessContextsEqual(PInvoke.GetWindowDpiAwarenessContext(child.Handle), context))
                {
                    child.Dispose();
                    throw new InvalidOperationException($"{label}: Windows did not preserve the requested child DPI context.");
                }
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
            using (DpiThreadScope.Awareness(PInvoke.GetWindowDpiAwarenessContext(hwnd)))
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
        if (host.Surface is { } surface)
        {
            // Redirection exposes child backing pixels, not Windows' DPI-scaled native image.
            double factor = 1;
            if (_compensate.IsChecked == true)
            {
                uint childDpi = PInvoke.GetDpiForWindow((HWND)host.WindowHandle);
                uint parentDpi = PInvoke.GetDpiForWindow((HWND)Microsoft.UI.Win32Interop.GetWindowFromWindowId(host.XamlRoot.ContentIslandEnvironment.AppWindowId));
                if (childDpi == 0 || parentDpi == 0)
                {
                    throw new InvalidOperationException("Could not query parent/child DPI for the experiment.");
                }
                factor = (double)parentDpi / childDpi;
            }
            var visual = surface.PlacementVisual;
            visual.Opacity = _mode.SelectedIndex == 1 ? 0 : 1;
            visual.Scale = new System.Numerics.Vector3((float)(factor / host.Placement.Scale), (float)(factor / host.Placement.Scale), 1);
            visual.Size = new System.Numerics.Vector2((float)(host.Placement.Bounds.Width / factor), (float)(host.Placement.Bounds.Height / factor));
        }
    }
}
