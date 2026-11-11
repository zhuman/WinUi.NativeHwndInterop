using System.Windows.Automation;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;

namespace WinUiHwndIntegration.Interop;

internal sealed class PluginWpfContent : Controls.Grid
{
    private readonly Controls.TextBox _first = new() { Text = "Type in the other process", Width = 260, Height = 26, TabIndex = 0 };
    private readonly Controls.TextBox _last = new() { Text = "Tab here, then back to WinUI", Width = 300, Height = 26, TabIndex = 2 };
    private readonly Controls.TextBlock _ticks = new();
    private int _clicks;

    public PluginWpfContent(Action<bool> leave)
    {
        Background = new Media.SolidColorBrush(Media.Color.FromRgb(230, 243, 255));
        var title = new Controls.TextBlock { Text = $"Out-of-process WPF - PID {Environment.ProcessId}" };
        var button = new Controls.Button { Content = "Plugin clicks: 0", Width = 200, Height = 32, TabIndex = 1 };
        button.Click += (_, _) => button.Content = $"Plugin clicks: {++_clicks}";
        AutomationProperties.SetName(_first, "Plugin first input");
        AutomationProperties.SetName(button, "Plugin counter");
        AutomationProperties.SetName(_last, "Plugin last input");
        Add(title, 12);
        Add(_first, 45);
        Add(button, 82);
        Add(_last, 128);
        Add(_ticks, 168);
        Controls.Control[] tabStops = [_first, button, _last];
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Input.Key.Tab || (Input.Keyboard.Modifiers & ~Input.ModifierKeys.Shift) != 0) return;
            int current = Array.FindIndex(tabStops, control => control.IsKeyboardFocusWithin);
            if (current < 0) return;
            args.Handled = true;
            bool forward = (Input.Keyboard.Modifiers & Input.ModifierKeys.Shift) == 0;
            int next = current + (forward ? 1 : -1);
            if (next < 0 || next >= tabStops.Length) leave(forward);
            else if (!tabStops[next].Focus()) throw new InvalidOperationException("Could not move focus within the WPF plugin.");
        };
    }

    public bool FocusEntry(bool forward) => (forward ? _first : _last).Focus();

    public void UpdateTicks(int ticks) => _ticks.Text = $"Plugin render ticks: {ticks}; WPF DPI {Media.VisualTreeHelper.GetDpi(this).PixelsPerInchX:F0}";

    private void Add(Wpf.FrameworkElement element, double top)
    {
        element.HorizontalAlignment = Wpf.HorizontalAlignment.Left;
        element.VerticalAlignment = Wpf.VerticalAlignment.Top;
        element.Margin = new Wpf.Thickness(12, top, 0, 0);
        Children.Add(element);
    }
}
