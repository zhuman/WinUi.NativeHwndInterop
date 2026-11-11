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
    private static readonly HashSet<RemoteHwndContent> s_sessions = [];
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _status;
    private readonly NamedPipeServerStream _pipe;
    private readonly Channel<PluginMessage> _outgoing = Channel.CreateUnbounded<PluginMessage>(new() { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipeName = $"WinUiHwndPlugin-{Guid.NewGuid():N}";
    private readonly Task _session;
    private Process? _process;
    private readonly PluginLaunchOptions _options;
    private readonly AppContainerLaunch? _sandbox;
    private bool _ready;
    private bool _stopping;
    private bool _ended;
    private bool _cleanupComplete;
    private volatile bool _disposed;
    private (int Width, int Height) _lastSize;

    public nint WindowHandle { get; }
    public event EventHandler<NativeFocusNavigationEventArgs>? FocusNavigationRequested;

    public RemoteHwndContent(nint parent, DispatcherQueue dispatcher, PluginLaunchOptions options, Action<string> status)
    {
        _dispatcher = dispatcher;
        _status = status;
        _options = options;
        try
        {
            string payload = Path.Combine(AppContext.BaseDirectory, "Plugin");
            if (!File.Exists(Path.Combine(payload, "WinUiHwndIntegration.Plugin.exe")))
                throw new FileNotFoundException("The companion executable was not deployed with the sample.");
            if (options.Isolation == PluginIsolation.AppContainer)
            {
                _sandbox = new AppContainerLaunch(payload);
                _pipe = _sandbox.CreatePipe(_pipeName);
            }
            else
            {
                _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            }
            WindowHandle = CreateContainer(parent);
        }
        catch
        {
            _pipe?.Dispose();
            _sandbox?.Dispose();
            _stop.Dispose();
            throw;
        }
        s_sessions.Add(this);
        _session = RunAsync();
    }

    public static async Task ShutdownAllAsync()
    {
        RemoteHwndContent[] sessions = [.. s_sessions];
        foreach (var session in sessions) session.RequestStop();
        await Task.WhenAll(sessions.Select(session => session._session));
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

    public void Resize()
    {
        if (_disposed || _stopping || _ended) return;
        RECT bounds;
        using (DpiThreadScope.AwarenessForWindow(WindowHandle))
        {
            if (!PInvoke.GetClientRect((HWND)WindowHandle, out bounds))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not query plugin container client size.");
        }
        if (bounds.Width <= 0 || bounds.Height <= 0 || _lastSize == (bounds.Width, bounds.Height)) return;
        _lastSize = (bounds.Width, bounds.Height);
        Send(new(PluginMessageKind.Resize, bounds.Width, bounds.Height));
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
        string? failure = null;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            string[] arguments = [WindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture),
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture), _pipeName, _options.Attachment.ToString(), _options.DpiAwareness.ToString(), _options.Framework.ToString()];
            if (_sandbox is not null)
            {
                _process = _sandbox.Start(arguments);
            }
            else
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Plugin", "WinUiHwndIntegration.Plugin.exe")) { UseShellExecute = false };
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                _process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the plugin process.");
            }
            string tokenStatus = AppContainerLaunch.VerifyToken(_process, _sandbox?.PackageSid);
            Task exit = _process.WaitForExitAsync();
            connect = _pipe.WaitForConnectionAsync(startup.Token);
            if (await Task.WhenAny(connect, exit) == exit)
            {
                throw new InvalidOperationException($"Plugin exited before connecting (exit code {_process.ExitCode}).");
            }
            await connect;
            VerifyClient(_process.Id);
            PluginMessage ready = await PluginProtocol.ReadAsync(_pipe, startup.Token);
            if (ready.Kind == PluginMessageKind.Error)
                throw new Win32Exception((int)ready.A,
                    $"Plugin {(ready.B == 1 ? "parent validation" : "HWND creation")} failed (code 0x{ready.A:X}): {new Win32Exception((int)ready.A).Message}");
            if (ready.Kind != PluginMessageKind.Ready || ready.B != _process.Id)
                throw new InvalidDataException("Plugin did not send a valid ready handshake.");
            nint child = checked((nint)ready.A);
            int pluginPid = _process.Id;
            var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (_disposed || _stopping || _ended) throw new OperationCanceledException(_stop.Token);
                    AttachChild(child, pluginPid);
                    Send(new(PluginMessageKind.Attached));
                    Resize();
                    _ready = true;
                    _status($"Connected: host PID {Environment.ProcessId}, plugin PID {pluginPid}, child HWND 0x{child:X}; {_options.Framework}; {_options.Attachment}; {tokenStatus}; {_options.DpiAwareness}, container/child DPI {PInvoke.GetDpiForWindow((HWND)WindowHandle)}/{PInvoke.GetDpiForWindow((HWND)child)}.");
                    attached.SetResult();
                }
                catch (Exception error) { attached.SetException(error); }
            })) throw new InvalidOperationException("The WinUI dispatcher is shutting down.");
            await attached.Task.WaitAsync(startup.Token);
            read = ReadMessagesAsync();
            write = WriteMessagesAsync();
            Task finished = await Task.WhenAny(read, write, exit);
            await finished;
            if (!_disposed && !_stopping)
            {
                throw new IOException(finished == exit ? $"Plugin exited unexpectedly (exit code {_process.ExitCode})." : "Plugin connection ended unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (_disposed || _stopping) { }
        catch (Exception error)
        {
            if (_disposed || _stopping)
            {
                Trace.TraceInformation($"Plugin session ended during shutdown: {error}");
            }
            else
            {
                Trace.TraceError($"Cross-process plugin failed: {error}");
                failure = $"Plugin failed: {error.Message}";
                Dispatch(() => _status(failure));
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
            try { if (_sandbox is not null) await Task.Run(_sandbox.Dispose); }
            catch (Exception error)
            {
                Trace.TraceError($"AppContainer cleanup failed: {error}");
                Dispatch(() => _status($"{failure}\nAppContainer cleanup failed: {error.Message}"));
            }
            Dispatch(() =>
            {
                _ready = false;
                _cleanupComplete = true;
                s_sessions.Remove(this);
                _stop.Dispose();
                if (_disposed) DestroyContainer();
            }, allowDisposed: true);
        }
    }

    private unsafe void VerifyClient(int expectedPid)
    {
        uint pid;
        if (!PInvoke.GetNamedPipeClientProcessId(new HANDLE(_pipe.SafePipeHandle.DangerousGetHandle()), &pid))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not verify plugin pipe client.");
        if (pid != expectedPid) throw new InvalidDataException("Unexpected plugin pipe client process.");
    }

    private unsafe void AttachChild(nint child, int expectedPid)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId((HWND)child, &pid);
        if (!PInvoke.IsWindow((HWND)child) || pid != expectedPid ||
            (PInvoke.GetWindowLong((HWND)child, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & (int)WINDOW_STYLE.WS_CHILD) == 0)
            throw new InvalidDataException("Plugin HWND is not a WS_CHILD owned by the launched process.");
        if (!PInvoke.AreDpiAwarenessContextsEqual(PInvoke.GetWindowDpiAwarenessContext((HWND)child), PInvoke.GetWindowDpiAwarenessContext((HWND)WindowHandle)))
            throw new InvalidOperationException("Plugin DPI awareness does not match the local container; refusing a potentially resetting cross-process attachment.");
        if (_options.Attachment == PluginAttachment.Broker)
        {
            HWND parking = PInvoke.GetParent((HWND)child);
            PInvoke.GetWindowThreadProcessId(parking, &pid);
            if (parking.IsNull || pid != expectedPid) throw new InvalidDataException("Plugin parking parent is not owned by the launched process.");
            using var awareness = DpiThreadScope.AwarenessForWindow(WindowHandle);
            if (PInvoke.SetParent((HWND)child, (HWND)WindowHandle).IsNull)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Broker SetParent failed.");
        }
        if (PInvoke.GetParent((HWND)child) != (HWND)WindowHandle)
            throw new InvalidDataException("Plugin HWND is not a direct child of the registered container.");
        if (!PInvoke.AreDpiAwarenessContextsEqual(PInvoke.GetWindowDpiAwarenessContext((HWND)child), PInvoke.GetWindowDpiAwarenessContext((HWND)WindowHandle)))
            throw new InvalidOperationException("Plugin DPI awareness changed during attachment.");
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

    private void DestroyContainer()
    {
        if (PInvoke.IsWindow((HWND)WindowHandle) && !PInvoke.DestroyWindow((HWND)WindowHandle))
            _status($"Plugin container cleanup failed: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        if (_cleanupComplete)
        {
            DestroyContainer();
            return;
        }
        RequestStop();
        PInvoke.ShowWindowAsync((HWND)WindowHandle, SHOW_WINDOW_CMD.SW_HIDE);
    }

    private void RequestStop()
    {
        if (_stopping || _ended) return;
        _stopping = true;
        _ready = false;
        Send(new(PluginMessageKind.Shutdown));
        _outgoing.Writer.TryComplete();
        // Do not wait on the UI thread: creating/destroying foreign child HWNDs may send it messages.
        _stop.CancelAfter(TimeSpan.FromSeconds(2));
    }
}
