using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUI.NativeHwndInterop;
using WinUI.NativeHwndInterop.WinForms;
using WinUI.NativeHwndInterop.Wpf;
using WinUiHwndIntegration.Interop;

namespace WinUiHwndIntegration.Pages;

public sealed partial class LibrarySamplesPage : Page
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<NativeHwndHost> _hosts = [];
    private System.Windows.Forms.Control? _formsChild;
    private WinFormsHwndHost? _formsHost;
    private WpfHwndHost? _wpfHost;

    public LibrarySamplesPage()
    {
        InitializeComponent();
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = "Native HWND interop libraries",
            FontSize = 24,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Three hosts using the same composition and input bridge. Tab and Shift+Tab between the XAML text boxes and each native subtree. Defaults: alpha 1, never cloaked, visible-area hit-test region. Native wheel input is not automatically chained to the XAML ScrollViewer.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(_status);

        var native = new NativeHwndHost
        {
            ChildWindowFactory = parent =>
            {
                var child = new SampleChildWindow((Windows.Win32.Foundation.HWND)parent);
                return new NativeWindowContent(child.NativeHandle, child.Dispose);
            },
        };
        AddSample(panel, "WinUI.NativeHwndInterop - native Win32", native);

        var formsPanel = new System.Windows.Forms.Panel();
        formsPanel.Controls.Add(new System.Windows.Forms.TextBox
        {
            Location = new System.Drawing.Point(16, 16),
            Width = 250,
            Text = "WinForms text input",
            TabIndex = 0,
        });
        var formsButton = new System.Windows.Forms.Button
        {
            Location = new System.Drawing.Point(16, 60),
            Width = 250,
            Text = "WinForms button",
            TabIndex = 1,
        };
        int formsClicks = 0;
        formsButton.Click += (_, _) => formsButton.Text = $"WinForms clicks: {++formsClicks}";
        formsPanel.Controls.Add(formsButton);
        var forms = new WinFormsHwndHost { Child = formsPanel };
        _formsChild = formsPanel;
        _formsHost = forms;
        AddSample(panel, "WinUI.NativeHwndInterop.WinForms", forms);

        var wpfPanel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
        wpfPanel.Children.Add(new System.Windows.Controls.TextBox
        {
            Text = "WPF text input",
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });
        var wpfButton = new System.Windows.Controls.Button
        {
            Content = "WPF button",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new System.Windows.Thickness(16, 8, 16, 8),
        };
        int wpfClicks = 0;
        wpfButton.Click += (_, _) => wpfButton.Content = $"WPF clicks: {++wpfClicks}";
        wpfPanel.Children.Add(wpfButton);
        var wpf = new WpfHwndHost { Child = wpfPanel };
        _wpfHost = wpf;
        AddSample(panel, "WinUI.NativeHwndInterop.Wpf", wpf);

        Content = new ScrollViewer { Content = panel };
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs args)
    {
        foreach (NativeHwndHost host in _hosts)
        {
            host.ChildWindowFactory = null;
        }
        if (_formsHost is not null)
        {
            _formsHost.Child = null;
        }
        if (_wpfHost is not null)
        {
            _wpfHost.Child = null;
        }
        _formsChild?.Dispose();
        _formsChild = null;
        base.OnNavigatedFrom(args);
    }

    private void AddSample(StackPanel panel, string title, NativeHwndHost host)
    {
        host.Width = 480;
        host.Height = 240;
        host.HorizontalAlignment = HorizontalAlignment.Left;
        host.HostingFailed += (_, args) => _status.Text = $"{title}: {args.Exception}";
        _hosts.Add(host);
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18 });
        panel.Children.Add(new TextBox { Header = $"Before {title}", PlaceholderText = "Tab into native content" });
        panel.Children.Add(host);
        panel.Children.Add(new TextBox { Header = $"After {title}", PlaceholderText = "Shift+Tab into native content" });
    }
}
