using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI.NativeHwndInterop;
using WinUiHwndIntegration.Interop;

namespace WinUiHwndIntegration.Pages;

public sealed partial class CrossProcessPage : Page
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly NativeHwndHost _host = new()
    {
        Height = 230,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        ClipMode = HwndClipMode.None,
    };
    private readonly ComboBox _mode = new()
    {
        Header = "Rendering comparison",
        ItemsSource = new[] { "Redirected + input (alpha 1)", "Native only (alpha 255)", "Redirected only (cloaked)" },
        SelectedIndex = 0,
    };
    private readonly ComboBox _attachment = new()
    {
        Header = "Attachment (restart to apply)",
        ItemsSource = new[] { "Direct child creation", "Broker SetParent" },
        SelectedIndex = 0,
    };
    private readonly ComboBox _isolation = new()
    {
        Header = "Process isolation (restart to apply)",
        ItemsSource = new[] { "Normal token", "AppContainer (none requested)" },
        SelectedIndex = 0,
    };
    private readonly ComboBox _dpi = new()
    {
        Header = "Container + plugin DPI (restart to apply)",
        ItemsSource = new[] { "PerMonitorV2", "Unaware (96 DPI)", "System aware" },
        SelectedIndex = 0,
    };
    private readonly ComboBox _framework = new()
    {
        Header = "Plugin framework (restart to apply)",
        ItemsSource = new[] { "WinForms", "WPF" },
        SelectedIndex = 0,
    };
    private readonly TextBlock _geometry = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private RemoteHwndContent? _content;

    public CrossProcessPage()
    {
        InitializeComponent();
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Cross-process child HWND", FontSize = 24 });
        panel.Children.Add(new TextBlock
        {
            Text = "The companion creates its own WS_CHILD containing WinForms or WPF UI. WPF uses ElementHost inside the companion; both frameworks and all their controls run out of process. Choose direct creation or a hidden parking parent followed by broker-performed SetParent. AppContainer requests no capabilities; Windows may add token SIDs, whose count is shown below. Direct AppContainer parenting is denied on the tested system; use broker attachment. For mixed DPI, the local container and plugin share the selected awareness beneath the mixed-hosting WinUI window; opt-in library scaling corrects redirected backing pixels. Restart to apply options.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Experimental, cooperative UI only. AppContainer reduces resource access, not cross-process HWND input-queue coupling or hangs. No unrestricted fallback is launched. Regions are disabled; keep the host fully visible when checking input. Core same-thread validation is unchanged.",
            TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var start = new Button { Content = "Start / restart plugin" };
        var stop = new Button { Content = "Stop plugin" };
        start.Click += (_, _) => Start();
        stop.Click += (_, _) => Stop();
        buttons.Children.Add(start);
        buttons.Children.Add(stop);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        options.Children.Add(_attachment);
        options.Children.Add(_isolation);
        options.Children.Add(_dpi);
        panel.Children.Add(options);
        panel.Children.Add(_framework);
        panel.Children.Add(buttons);
        panel.Children.Add(_mode);
        panel.Children.Add(_status);
        panel.Children.Add(_geometry);
        panel.Children.Add(new TextBox { Header = "Before plugin", PlaceholderText = "Tab into the other process" });
        panel.Children.Add(_host);
        panel.Children.Add(new TextBox { Header = "After plugin", PlaceholderText = "Shift+Tab into the other process" });
        panel.Children.Add(new Border { Height = 120 });
        Content = new ScrollViewer { Content = panel };
        _mode.SelectionChanged += (_, _) => ApplyOptions();
        _host.StateChanged += (_, _) =>
        {
            ApplyOptions();
            _content?.Resize();
            _geometry.Text = _host.WindowHandle == 0 ? "" :
                $"XAML scale {_host.XamlRoot.RasterizationScale:F2}; source scale factor {_host.Surface?.NativeScaleFactor:F2}; physical host {_host.Placement.Bounds.Width}x{_host.Placement.Bounds.Height}px.";
        };
        _host.HostingFailed += (_, error) => _status.Text = $"Hosting failed: {error.Exception}";
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    private void Start()
    {
        Stop();
        var options = new PluginLaunchOptions((PluginAttachment)_attachment.SelectedIndex,
            (PluginIsolation)_isolation.SelectedIndex, (PluginDpiAwareness)_dpi.SelectedIndex,
            (PluginFramework)_framework.SelectedIndex);
        _host.ChildDpiAwareness = options.DpiAwareness switch
        {
            PluginDpiAwareness.Unaware => NativeDpiAwareness.Unaware,
            PluginDpiAwareness.SystemAware => NativeDpiAwareness.SystemAware,
            _ => NativeDpiAwareness.PerMonitorV2,
        };
        _host.DpiScalingMode = HwndDpiScalingMode.MatchNativeWindow;
        _status.Text = "Starting companion process...";
        _host.ChildWindowFactory = parent =>
        {
            RemoteHwndContent? instance = null;
            instance = new RemoteHwndContent(parent, DispatcherQueue, options, message =>
            {
                if (ReferenceEquals(_content, instance)) _status.Text = message;
            });
            _content = instance;
            return instance;
        };
    }

    private void Stop()
    {
        _host.ChildWindowFactory = null;
        _content = null;
        _status.Text = "Plugin stopped. Shutdown runs asynchronously; an unresponsive owned process is terminated after a bounded timeout.";
    }

    private void ApplyOptions()
    {
        int alpha = _mode.SelectedIndex == 1 ? 255 : 1;
        var cloak = _mode.SelectedIndex == 2 ? HwndCloakMode.AlwaysCloaked : HwndCloakMode.NeverCloaked;
        if (_host.NativeAlpha != alpha) _host.NativeAlpha = alpha;
        if (_host.CloakMode != cloak) _host.CloakMode = cloak;
        if (_host.Surface is { } surface) surface.PlacementVisual.Opacity = _mode.SelectedIndex == 1 ? 0 : 1;
    }
}
