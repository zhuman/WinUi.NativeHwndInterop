using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUiHwndIntegration.Pages;
using WinUiHwndIntegration.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUiHwndIntegration
{
    public sealed partial class MainWindow : Window
    {
        private bool _closing;
        private bool _canClose;

        public MainWindow()
        {
            InitializeComponent();
            AppTitleBar.Subtitle = ((App)Application.Current).CompositionStatus;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            AppWindow.SetIcon("Assets/AppIcon.ico");
            AppWindow.Closing += OnAppWindowClosing;
        }

        private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_canClose) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            await RemoteHwndContent.ShutdownAllAsync();
            _canClose = true;
            Close();
        }

        private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
        {
            NavView.IsPaneOpen = !NavView.IsPaneOpen;
        }

        private void TitleBar_BackRequested(TitleBar sender, object args)
        {
            NavFrame.GoBack();
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                NavFrame.Navigate(typeof(SettingsPage));
            }
            else if (args.SelectedItem is NavigationViewItem item)
            {
                switch (item.Tag)
                {
                    case "home":
                        NavFrame.Navigate(typeof(HomePage));
                        break;
                    case "about":
                        NavFrame.Navigate(typeof(AboutPage));
                        break;
                    case "libraries":
                        NavFrame.Navigate(typeof(LibrarySamplesPage));
                        break;
                    case "dpi":
                        NavFrame.Navigate(typeof(MixedDpiPage));
                        break;
                    case "process":
                        NavFrame.Navigate(typeof(CrossProcessPage));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
                }
            }
        }
    }
}
