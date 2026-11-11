using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinUI.NativeHwndInterop;
using WinUiHwndIntegration.Interop;

namespace WinUiHwndIntegration.Pages
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
            HwndHostElement.StateChanged += (_, _) => UpdateStatus();
            HwndHostElement.HostingFailed += (_, args) => AlphaErrorText.Text = $"Hosting failed: {args.Exception}";
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            HwndHostElement.ChildWindowFactory = parent =>
            {
                var child = new SampleChildWindow((Windows.Win32.Foundation.HWND)parent);
                return new NativeWindowContent(child.NativeHandle, child.Dispose);
            };
            UpdateStatus();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e) => HwndHostElement.ChildWindowFactory = null;

        private void CloakModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HwndHostElement is not null && CloakModeRadios.SelectedIndex >= 0)
            {
                HwndHostElement.CloakMode = (HwndCloakMode)CloakModeRadios.SelectedIndex;
            }
        }

        private void ClipModeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HwndHostElement is not null && ClipModeRadios.SelectedIndex >= 0)
            {
                HwndHostElement.ClipMode = (HwndClipMode)ClipModeRadios.SelectedIndex;
            }
        }

        private void SlideButton_Click(object sender, RoutedEventArgs e) => SlideStoryboard.Begin();

        private void RotateToggle_Click(object sender, RoutedEventArgs e) =>
            HostTransform.Rotation = RotateToggle.IsChecked == true ? 6 : 0;

        private void ChildAlphaSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (HwndHostElement is not null)
            {
                HwndHostElement.NativeAlpha = (int)e.NewValue;
                UpdateStatus();
            }
        }

        private void UpdateStatus()
        {
            if (HwndHostElement is null || StatusText is null)
            {
                return;
            }

            HwndPlacement p = HwndHostElement.Placement;
            PixelRect b = p.Bounds;
            PixelRect v = p.Visible;
            StatusText.Text =
                $"Child HWND: 0x{HwndHostElement.WindowHandle:X}   Native alpha: {HwndHostElement.NativeAlpha}/255\n" +
                $"HWND bounds (px): {b.Left},{b.Top} {b.Width}x{b.Height}   visible: {v.Left},{v.Top} {v.Width}x{v.Height}\n" +
                $"Window region: {HwndHostElement.DescribeWindowRegion()}   SetWindowRgn error: {HwndHostElement.LastRegionError}\n" +
                $"Cloaked: {HwndHostElement.IsCloaked} ({(HwndHostElement.CloakReason is { Length: > 0 } r ? r : "uncloaked")})   DWMWA_CLOAK hr=0x{HwndHostElement.LastCloakResult:X8}";
        }
    }
}
