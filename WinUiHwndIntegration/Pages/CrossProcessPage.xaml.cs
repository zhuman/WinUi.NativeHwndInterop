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
    private RemoteHwndContent? _content;

    public CrossProcessPage()
    {
        InitializeComponent();
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Cross-process child HWND", FontSize = 24 });
        panel.Children.Add(new TextBlock
        {
            Text = "A second instance of this executable runs in companion mode, without starting WinUI. It receives a local layered container HWND, creates its own WS_CHILD beneath it, and runs its own WinForms message loop. The library redirects the local container, including the remote subtree. Compare cloaked redirection with native-only rendering to check the captured pixels. Tab, Shift+Tab, size and shutdown use an asynchronous, same-user named pipe. Both processes use PerMonitorV2. Regions start disabled to isolate the experiment.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Trusted cooperative plugins only: cross-process parenting can couple Windows input queues. This is not a sandbox or protection against a hung plugin. The core library's same-thread HWND requirements are unchanged.",
            TextWrapping = TextWrapping.Wrap,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var start = new Button { Content = "Start / restart plugin" };
        var stop = new Button { Content = "Stop plugin" };
        start.Click += (_, _) => Start();
        stop.Click += (_, _) => Stop();
        buttons.Children.Add(start);
        buttons.Children.Add(stop);
        panel.Children.Add(buttons);
        panel.Children.Add(_mode);
        panel.Children.Add(_status);
        panel.Children.Add(new TextBox { Header = "Before plugin", PlaceholderText = "Tab into the other process" });
        panel.Children.Add(_host);
        panel.Children.Add(new TextBox { Header = "After plugin", PlaceholderText = "Shift+Tab into the other process" });
        panel.Children.Add(new Border { Height = 120 });
        Content = new ScrollViewer { Content = panel };
        _mode.SelectionChanged += (_, _) => ApplyOptions();
        _host.StateChanged += (_, _) =>
        {
            ApplyOptions();
            _content?.Resize(_host.Placement.Bounds.Width, _host.Placement.Bounds.Height);
        };
        _host.HostingFailed += (_, error) => _status.Text = $"Hosting failed: {error.Exception}";
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    private void Start()
    {
        Stop();
        _status.Text = "Starting companion process...";
        _host.ChildWindowFactory = parent =>
        {
            RemoteHwndContent? instance = null;
            instance = new RemoteHwndContent(parent, DispatcherQueue, message =>
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
