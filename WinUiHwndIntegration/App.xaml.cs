using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUiHwndIntegration
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private readonly Microsoft.UI.Composition.CompositionEngineType? _requestedEngine = Program.RequestedEngine;

        public string CompositionStatus { get; private set; } = "Composition engine not yet verified";

        /// <summary>
        /// Initializes the application after Program has selected the composition engine.
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            var actualEngine = Microsoft.UI.Composition.CompositionEngine.GetForSystemEngine(compositor) is not null
                ? Microsoft.UI.Composition.CompositionEngineType.System
                : Microsoft.UI.Composition.CompositionEngineType.InProcess;
            if (_requestedEngine is { } requested && actualEngine != requested)
            {
                throw new InvalidOperationException($"Requested {requested} composition, but XAML is using {actualEngine}.");
            }
            CompositionStatus = $"Composition: {actualEngine} (requested: {_requestedEngine?.ToString() ?? "Default"})";
            using (WinUI.NativeHwndInterop.DpiThreadScope.MixedHosting())
            {
                _window = new MainWindow { Title = $"WinUiHwndIntegration - {CompositionStatus}" };
            }
            _window.Activate();
        }
    }
}
