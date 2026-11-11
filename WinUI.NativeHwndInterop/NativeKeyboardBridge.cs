using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinUI.NativeHwndInterop;

internal static unsafe class NativeKeyboardBridge
{
    [ThreadStatic] private static List<NativeHwndHost>? s_hosts;
    [ThreadStatic] private static HHOOK s_hook;
    [ThreadStatic] private static bool s_processing;

    internal static void Register(NativeHwndHost host)
    {
        s_hosts ??= [];
        if (s_hosts.Contains(host))
        {
            return;
        }
        if (s_hosts.Count == 0)
        {
            s_hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_GETMESSAGE, &OnMessage,
                HINSTANCE.Null, PInvoke.GetCurrentThreadId());
            if (s_hook.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not install native keyboard preprocessing.");
            }
        }
        s_hosts.Add(host);
    }

    internal static void Unregister(NativeHwndHost host)
    {
        if (s_hosts?.Remove(host) == true && s_hosts.Count == 0 && !s_hook.IsNull)
        {
            if (!PInvoke.UnhookWindowsHookEx(s_hook))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not remove native keyboard preprocessing.");
            }
            s_hook = default;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT OnMessage(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0 && wParam.Value == 1 && !s_processing && s_hosts is { Count: > 0 })
        {
            MSG* msg = (MSG*)lParam.Value;
            if (msg->message is >= 0x0100 and <= 0x0109)
            {
                NativeHwndHost? owner = null;
                s_processing = true;
                try
                {
                    owner = s_hosts.Find(host => host.OwnsWindow(msg->hwnd));
                    if (owner is not null)
                    {
                        NativeMessage message = new()
                        {
                            Window = (nint)msg->hwnd.Value,
                            Id = msg->message,
                            WParam = msg->wParam.Value,
                            LParam = msg->lParam.Value,
                            Time = msg->time,
                            X = msg->pt.X,
                            Y = msg->pt.Y,
                        };
                        if (owner.Preprocess(ref message))
                        {
                            msg->message = PInvoke.WM_NULL;
                            msg->wParam = default;
                            msg->lParam = default;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Exceptions cannot cross an unmanaged hook callback. Report on the UI dispatcher instead.
                    System.Diagnostics.Trace.TraceError($"Native keyboard preprocessing failed: {ex}");
                    if (owner is not null)
                    {
                        NativeHwndHost failedHost = owner;
                        if (!owner.DispatcherQueue.TryEnqueue(() => failedHost.ReportFailure(ex)))
                        {
                            System.Diagnostics.Trace.TraceError("The UI dispatcher could not deliver the hosting failure.");
                        }
                    }
                }
                finally
                {
                    s_processing = false;
                }
            }
        }
        return PInvoke.CallNextHookEx(s_hook, code, wParam, lParam);
    }
}
