using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.UI.Dispatching;
using WinUI.NativeHwndInterop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinUiHwndIntegration.Interop;

/// <summary>Sample-only cooperative out-of-process content below an in-process layered container.</summary>
internal sealed class RemoteHwndContent : INativeHwndContent
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _status;
    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<PluginMessage> _outgoing = Channel.CreateUnbounded<PluginMessage>(new() { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName = $"WinUiHwndPlugin-{Guid.NewGuid():N}";
    private readonly Task _session;
    private Process? _process;
    private bool _ready;
    private bool _ended;
    private volatile bool _disposed;
    private (int Width, int Height) _lastSize;

    public nint WindowHandle { get; }
    public event EventHandler<NativeFocusNavigationEventArgs>? FocusNavigationRequested;

    public RemoteHwndContent(nint parent, DispatcherQueue dispatcher, Action<string> status)
    {
        _dispatcher = dispatcher;
        _status = status;
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            WindowHandle = CreateContainer(parent);
        }
        catch
        {
            _pipe.Dispose();
            _stop.Dispose();
            throw;
        }
        _session = RunAsync();
    }

    private static unsafe nint CreateContainer(nint parent)
    {
        HWND hwnd;
        fixed (char* className = "STATIC")
        fixed (char* title = "Cross-process plugin container")
        {
            hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_LAYERED, className, title,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_CLIPCHILDREN | WINDOW_STYLE.WS_CLIPSIBLINGS,
                0, 0, 1, 1, (HWND)parent, HMENU.Null, HINSTANCE.Null, null);
        }
        if (hwnd.IsNull) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create the plugin container.");
        return (nint)hwnd.Value;
    }

    public void Resize(int width, int height)
    {
        if (_disposed || _ended || width <= 0 || height <= 0 || _lastSize == (width, height)) return;
        _lastSize = (width, height);
        Send(new(PluginMessageKind.Resize, width, height));
    }

    public bool Focus(bool forward)
    {
        if (_disposed || !_ready) return false;
        Send(new(PluginMessageKind.Focus, forward ? 1 : 0));
        return true;
    }

    public bool TranslateMessage(ref NativeMessage message) => false;

    private void Send(PluginMessage message)
    {
        if (!_outgoing.Writer.TryWrite(message) && !_disposed)
        {
            throw new InvalidOperationException("The plugin connection is closed.");
        }
    }

    private async Task RunAsync()
    {
        Task read = Task.CompletedTask;
        Task write = Task.CompletedTask;
        Task connect = Task.CompletedTask;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Application executable path is unavailable."))
            {
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--external-child");
            start.ArgumentList.Add(WindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add(_pipeName);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the plugin process.");
            Task exit = _process.WaitForExitAsync();
            connect = _pipe.WaitForConnectionAsync(startup.Token);
            if (await Task.WhenAny(connect, exit) == exit)
            {
                throw new InvalidOperationException($"Plugin exited before connecting (exit code {_process.ExitCode}).");
            }
            await connect;
            VerifyClient(_process.Id);
            PluginMessage ready = await PluginProtocol.ReadAsync(_pipe, startup.Token);
            if (ready.Kind != PluginMessageKind.Ready || ready.B != _process.Id)
            {
                throw new InvalidDataException("Plugin did not send a valid ready handshake.");
            }
            nint child = checked((nint)ready.A);
            int pluginPid = _process.Id;
            Dispatch(() =>
            {
                if (_ended) return;
                if (!IsExpectedChild(child, pluginPid))
                {
                    _status("Plugin failed: returned HWND does not belong to the launched process/container.");
                    Dispose();
                    return;
                }
                _ready = true;
                if (!PInvoke.GetClientRect((HWND)WindowHandle, out RECT bounds))
                {
                    _status($"Plugin sizing failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
                    Dispose();
                    return;
                }
                Resize(bounds.Width, bounds.Height);
                _status($"Connected: host PID {Environment.ProcessId}, plugin PID {pluginPid}, child HWND 0x{child:X}. Native input runs in the plugin process.");
            });
            read = ReadMessagesAsync();
            write = WriteMessagesAsync();
            Task finished = await Task.WhenAny(read, write, exit);
            await finished;
            if (!_disposed)
            {
                throw new IOException(finished == exit ? $"Plugin exited unexpectedly (exit code {_process.ExitCode})." : "Plugin connection ended unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception error)
        {
            if (_disposed)
            {
                Trace.TraceInformation($"Plugin session ended during shutdown: {error}");
            }
            else
            {
                Trace.TraceError($"Cross-process plugin failed: {error}");
                Dispatch(() => _status($"Plugin failed: {error.Message}"));
            }
        }
        finally
        {
            _ended = true;
            _ready = false;
            _stop.Cancel();
            _outgoing.Writer.TryComplete();
            _pipe.Dispose();
            try { await Task.WhenAll(connect, read, write); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { Trace.TraceInformation($"Plugin IPC ended: {error.Message}"); }
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch (TimeoutException)
                        {
                            Trace.TraceWarning($"Plugin PID {_process.Id} did not shut down; terminating the owned child process.");
                            _process.Kill();
                            await _process.WaitForExitAsync();
                        }
                    }
                }
                catch (Exception error)
                {
                    Trace.TraceError($"Plugin cleanup failed: {error}");
                    Dispatch(() => _status($"Plugin cleanup failed: {error.Message}"));
                }
                finally { _process.Dispose(); }
            }
            Dispatch(() =>
            {
                _ready = false;
                if (PInvoke.IsWindow((HWND)WindowHandle) && !PInvoke.DestroyWindow((HWND)WindowHandle))
                {
                    _status($"Plugin container cleanup failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
                }
            }, allowDisposed: true);
            _stop.Dispose();
        }
    }

    private unsafe void VerifyClient(int expectedPid)
    {
        uint pid;
        if (!PInvoke.GetNamedPipeClientProcessId(new HANDLE(_pipe.SafePipeHandle.DangerousGetHandle()), &pid))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not verify plugin pipe client.");
        if (pid != expectedPid) throw new InvalidDataException("Unexpected plugin pipe client process.");
    }

    private unsafe bool IsExpectedChild(nint child, int expectedPid)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId((HWND)child, &pid);
        return PInvoke.IsWindow((HWND)child) && PInvoke.GetParent((HWND)child) == (HWND)WindowHandle && pid == expectedPid;
    }

    private async Task ReadMessagesAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            PluginMessage message = await PluginProtocol.ReadAsync(_pipe, _stop.Token);
            if (message.Kind != PluginMessageKind.Leave || message.A is not (0 or 1))
                throw new InvalidDataException("Unexpected plugin message.");
            Dispatch(() =>
            {
                // Ignore delayed/repeated departures once focus has already left the remote subtree.
                if (PInvoke.IsChild((HWND)WindowHandle, PInvoke.GetFocus()))
                    FocusNavigationRequested?.Invoke(this, new NativeFocusNavigationEventArgs(message.A == 1));
            });
        }
    }

    private async Task WriteMessagesAsync()
    {
        await foreach (PluginMessage message in _outgoing.Reader.ReadAllAsync(_stop.Token))
            await PluginProtocol.WriteAsync(_pipe, message, _stop.Token);
    }

    private void Dispatch(Action callback, bool allowDisposed = false)
    {
        if (!_dispatcher.TryEnqueue(() => { if (allowDisposed || !_disposed) callback(); }))
            Trace.TraceWarning("Plugin callback could not be dispatched because the WinUI dispatcher is shutting down.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        if (_ended) return;
        Send(new(PluginMessageKind.Shutdown));
        _outgoing.Writer.TryComplete();
        // Do not wait on the UI thread: creating/destroying foreign child HWNDs may send it messages.
        _stop.CancelAfter(TimeSpan.FromSeconds(2));
        PInvoke.ShowWindowAsync((HWND)WindowHandle, SHOW_WINDOW_CMD.SW_HIDE);
    }
}
