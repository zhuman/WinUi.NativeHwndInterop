using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;
using Forms = System.Windows.Forms;

namespace WinUiHwndIntegration.Interop;

/// <summary>The sample executable's companion mode; never starts WinUI or creates a top-level window.</summary>
internal static class ExternalChildProcess
{
    public static void Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("Expected --external-child <parent HWND> <parent PID> <pipe name>.");
        nint parent = checked((nint)long.Parse(args[1], CultureInfo.InvariantCulture));
        int parentPid = int.Parse(args[2], CultureInfo.InvariantCulture);
        VerifyParent(parent, parentPid);
        using Process owner = Process.GetProcessById(parentPid);
        _ = ExitWithParentAsync(owner);
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        Forms.Application.EnableVisualStyles();
        Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException);

        using var pipe = new NamedPipeClientStream(".", args[3], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe.Connect(15000);
        VerifyServer(pipe, parentPid);
        VerifyParent(parent, parentPid);
        using var stop = new CancellationTokenSource();
        var outgoing = Channel.CreateUnbounded<PluginMessage>(new() { SingleReader = true, SingleWriter = true });
        using var context = new Forms.ApplicationContext();
        using var root = new PluginControl(parent, forward =>
        {
            if (!outgoing.Writer.TryWrite(new(PluginMessageKind.Leave, forward ? 1 : 0)))
                throw new IOException("The host connection is closed.");
        });
        _ = root.Handle;
        root.PerformLayout();
        if (!outgoing.Writer.TryWrite(new(PluginMessageKind.Ready, root.Handle.ToInt64(), Environment.ProcessId)))
            throw new IOException("Could not queue the plugin handshake.");
        Task communication = Task.Run(CommunicateAsync);
        try
        {
            Forms.Application.Run(context);
        }
        finally
        {
            stop.Cancel();
            outgoing.Writer.TryComplete();
            pipe.Dispose();
            communication.GetAwaiter().GetResult();
        }

        async Task CommunicateAsync()
        {
            Task read = ReadAsync();
            Task write = WriteAsync();
            try
            {
                await await Task.WhenAny(read, write);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error)
            {
                Trace.TraceError($"Plugin IPC failed: {error}");
                // A disconnected host must not leave an orphan, even if this UI thread is unresponsive.
                Environment.Exit(1);
            }
            finally
            {
                stop.Cancel();
                try { await Task.WhenAll(read, write); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { Trace.TraceError($"Plugin IPC shutdown: {error}"); }
            }
        }

        async Task ReadAsync()
        {
            while (true)
            {
                PluginMessage message = await PluginProtocol.ReadAsync(pipe, stop.Token);
                if (message.Kind == PluginMessageKind.Resize && message.A is > 0 and <= 32768 && message.B is > 0 and <= 32768)
                {
                    root.BeginInvoke(() => root.Size = new Size((int)message.A, (int)message.B));
                }
                else if (message.Kind == PluginMessageKind.Focus && message.A is 0 or 1)
                {
                    root.BeginInvoke(() =>
                    {
                        if (!root.SelectNextControl(null, message.A == 1, true, true, false) || root.ActiveControl?.Focus() != true)
                            throw new InvalidOperationException("Could not transfer keyboard focus into the plugin.");
                    });
                }
                else if (message.Kind == PluginMessageKind.Shutdown)
                {
                    root.BeginInvoke(context.ExitThread);
                    return;
                }
                else throw new InvalidDataException("Unexpected host command.");
            }
        }

        async Task WriteAsync()
        {
            await foreach (PluginMessage message in outgoing.Reader.ReadAllAsync(stop.Token))
                await PluginProtocol.WriteAsync(pipe, message, stop.Token);
        }
    }

    private static async Task ExitWithParentAsync(Process owner)
    {
        await owner.WaitForExitAsync();
        Environment.Exit(0);
    }

    private static unsafe void VerifyParent(nint parent, int expectedPid)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId((HWND)parent, &pid);
        if (!PInvoke.IsWindow((HWND)parent) || pid != expectedPid || expectedPid == Environment.ProcessId)
            throw new ArgumentException("Parent HWND does not belong to the specified host process.");
        if (!PInvoke.AreDpiAwarenessContextsEqual(PInvoke.GetWindowDpiAwarenessContext((HWND)parent), DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            throw new InvalidOperationException("This cross-process experiment requires a PerMonitorV2 parent.");
    }

    private static unsafe void VerifyServer(NamedPipeClientStream pipe, int expectedPid)
    {
        uint pid;
        if (!PInvoke.GetNamedPipeServerProcessId(new HANDLE(pipe.SafePipeHandle.DangerousGetHandle()), &pid))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not verify the host pipe server.");
        if (pid != expectedPid) throw new InvalidDataException("Unexpected host pipe server process.");
    }

    private sealed class PluginControl : Forms.ContainerControl
    {
        private readonly nint _parent;
        private readonly Action<bool> _leave;
        private readonly Forms.Timer _timer;
        private int _ticks;
        private int _clicks;

        public PluginControl(nint parent, Action<bool> leave)
        {
            _parent = parent;
            _leave = leave;
            BackColor = Color.FromArgb(230, 243, 255);
            AutoScaleMode = Forms.AutoScaleMode.None;
            Size = new Size(600, 230);
            var title = new Forms.Label { Text = $"Out-of-process child - PID {Environment.ProcessId}", AutoSize = true, Location = new(12, 12) };
            var first = new Forms.TextBox { AccessibleName = "Plugin first input", Text = "Type in the other process", Location = new(12, 45), Width = 260, TabIndex = 0 };
            var button = new Forms.Button { AccessibleName = "Plugin counter", Text = "Plugin clicks: 0", Location = new(12, 82), Size = new(200, 32), TabIndex = 1 };
            var last = new Forms.TextBox { AccessibleName = "Plugin last input", Text = "Tab here, then back to WinUI", Location = new(12, 128), Width = 300, TabIndex = 2 };
            var ticks = new Forms.Label { AutoSize = true, Location = new(12, 168) };
            button.Click += (_, _) => button.Text = $"Plugin clicks: {++_clicks}";
            Controls.AddRange([title, first, button, last, ticks]);
            Layout += (_, _) =>
            {
                float scale = DeviceDpi / 96f;
                title.Location = new((int)(12 * scale), (int)(12 * scale));
                first.SetBounds((int)(12 * scale), (int)(45 * scale), (int)(260 * scale), first.Height);
                button.SetBounds((int)(12 * scale), (int)(82 * scale), (int)(200 * scale), (int)(32 * scale));
                last.SetBounds((int)(12 * scale), (int)(128 * scale), (int)(300 * scale), last.Height);
                ticks.Location = new((int)(12 * scale), (int)(168 * scale));
            };
            DpiChangedAfterParent += (_, _) => PerformLayout();
            _timer = new() { Interval = 250 };
            _timer.Tick += (_, _) => ticks.Text = $"Plugin render ticks: {++_ticks}; DPI {DeviceDpi}";
            _timer.Start();
        }

        protected override Forms.CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.Parent = _parent;
                parameters.Style = (parameters.Style | 0x40000000 | 0x10000000) & ~unchecked((int)0x80000000); // WS_CHILD, WS_VISIBLE, not WS_POPUP.
                return parameters;
            }
        }

        protected override bool ProcessDialogKey(Forms.Keys keyData)
        {
            if ((keyData & (Forms.Keys.KeyCode | Forms.Keys.Control | Forms.Keys.Alt)) == Forms.Keys.Tab)
            {
                bool forward = (keyData & Forms.Keys.Shift) == 0;
                if (!SelectNextControl(ActiveControl, forward, true, true, false)) _leave(forward);
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
