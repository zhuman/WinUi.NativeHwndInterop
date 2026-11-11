using System.Diagnostics;
using WinUiHwndIntegration.Interop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            ExternalChildProcess.Run(args);
        }
        catch (Exception error)
        {
            Trace.TraceError($"Plugin startup failed: {error}");
            Environment.ExitCode = 1;
        }
    }
}
