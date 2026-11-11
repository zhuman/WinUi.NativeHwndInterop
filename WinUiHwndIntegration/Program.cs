using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinUiHwndIntegration;

internal static class Program
{
    internal static CompositionEngineType? RequestedEngine { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        string logDirectory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUiHwndIntegration");
        System.IO.Directory.CreateDirectory(logDirectory);
        bool externalChild = args.FirstOrDefault() == "--external-child";
        string logName = externalChild ? "plugin-error.log" : "startup-error.log";
        AppDomain.CurrentDomain.UnhandledException += (sender, error) =>
            System.IO.File.WriteAllText(System.IO.Path.Combine(logDirectory, logName), error.ExceptionObject.ToString());

        if (externalChild)
        {
            Interop.ExternalChildProcess.Run(args);
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        bool system = args.Contains("--system-composition");
        bool inProcess = args.Contains("--in-process-composition");
        if (system && inProcess)
        {
            throw new ArgumentException("Choose either --system-composition or --in-process-composition, not both.");
        }
        RequestedEngine = system ? CompositionEngineType.System
            : inProcess ? CompositionEngineType.InProcess : null;

        // Select the process engine before Application.Start can create a compositor.
        if (RequestedEngine is { } engine && !CompositionEngine.TrySetProcessEngine(engine))
        {
            throw new InvalidOperationException(
                $"The requested {engine} composition engine could not be enabled. " +
                "System composition is a Limited Access Feature requiring Microsoft approval; " +
                "also verify OS/runtime support. No fallback was tested.");
        }
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
