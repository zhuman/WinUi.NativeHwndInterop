using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinUiHwndIntegration.Interop;

/// <summary>
/// A plain Win32 window created as a WS_CHILD + WS_EX_LAYERED child of the WinUI top-level window.
/// It hosts a real Win32 BUTTON and repaints a tick counter on a timer, so it's easy to tell whether
/// the DirectComposition surface created from it is live.
/// </summary>
internal sealed unsafe class SampleChildWindow : IDisposable
{
    private const string ClassName = "WinUiHwndIntegration.SampleChildWindow";
    private const int ButtonId = 1001;
    private const nuint TickTimerId = 1;

    private static readonly Dictionary<HWND, SampleChildWindow> s_windows = [];
    private static bool s_classRegistered;

    private HWND _button;
    private int _clickCount;
    private int _tickCount;

    public HWND Handle { get; private set; }

    public nint NativeHandle => (nint)Handle.Value;

    public byte Alpha { get; private set; }

    public SampleChildWindow(HWND parent)
    {
        EnsureClassRegistered();

        HINSTANCE hInstance = (HINSTANCE)(nint)PInvoke.GetModuleHandle((PCWSTR)null).Value;

        fixed (char* className = ClassName)
        fixed (char* windowName = "Sample child HWND")
        {
            Handle = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_LAYERED,
                className,
                windowName,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_CLIPCHILDREN | WINDOW_STYLE.WS_CLIPSIBLINGS,
                0, 0, 0, 0,
                parent,
                HMENU.Null,
                hInstance,
                null);
        }

        if (Handle.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed for the sample child window.");
        }

        s_windows[Handle] = this;

        // A layered window has no content until SetLayeredWindowAttributes/UpdateLayeredWindow is called.
        SetAlpha(255);

        float dpiScale = PInvoke.GetDpiForWindow(Handle) / 96f;
        fixed (char* buttonClass = "BUTTON")
        fixed (char* buttonText = "Win32 button")
        {
            _button = PInvoke.CreateWindowEx(
                0,
                buttonClass,
                buttonText,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | WINDOW_STYLE.WS_TABSTOP | (WINDOW_STYLE)PInvoke.BS_PUSHBUTTON,
                (int)(16 * dpiScale), (int)(96 * dpiScale), (int)(140 * dpiScale), (int)(32 * dpiScale),
                Handle,
                (HMENU)(nint)ButtonId,
                hInstance,
                null);
        }

        PInvoke.SendMessage(_button, PInvoke.WM_SETFONT, (WPARAM)(nuint)(nint)PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.DEFAULT_GUI_FONT).Value, (LPARAM)1);
        PInvoke.SetTimer(Handle, TickTimerId, 250, null);
    }

    public void SetAlpha(byte alpha)
    {
        if (!PInvoke.SetLayeredWindowAttributes(Handle, default, alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetLayeredWindowAttributes failed.");
        }
        Alpha = alpha;
    }

    public void Dispose()
    {
        if (Handle.IsNull)
        {
            return;
        }

        PInvoke.KillTimer(Handle, TickTimerId);
        PInvoke.DestroyWindow(Handle);
        s_windows.Remove(Handle);
        Handle = HWND.Null;
        _button = HWND.Null;
    }

    private static void EnsureClassRegistered()
    {
        if (s_classRegistered)
        {
            return;
        }

        fixed (char* className = ClassName)
        {
            WNDCLASSEXW wc = new()
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
                lpfnWndProc = &WndProc,
                hInstance = (HINSTANCE)(nint)PInvoke.GetModuleHandle((PCWSTR)null).Value,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                lpszClassName = className,
            };

            if (PInvoke.RegisterClassEx(&wc) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx failed.");
            }
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_windows.TryGetValue(hwnd, out SampleChildWindow? window))
            {
                switch (msg)
                {
                    case PInvoke.WM_PAINT:
                        window.OnPaint();
                        return (LRESULT)0;

                    case PInvoke.WM_ERASEBKGND:
                        return (LRESULT)1;

                    case PInvoke.WM_TIMER when wParam.Value == TickTimerId:
                        window._tickCount++;
                        PInvoke.InvalidateRect(hwnd, (RECT?)null, false);
                        return (LRESULT)0;

                    case PInvoke.WM_COMMAND when (wParam.Value & 0xFFFF) == ButtonId && ((wParam.Value >> 16) & 0xFFFF) == PInvoke.BN_CLICKED:
                        window._clickCount++;
                        PInvoke.InvalidateRect(hwnd, (RECT?)null, false);
                        return (LRESULT)0;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SampleChildWindow.WndProc: {ex}");
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnPaint()
    {
        HDC hdc = PInvoke.BeginPaint(Handle, out PAINTSTRUCT ps);
        try
        {
            PInvoke.GetClientRect(Handle, out RECT client);

            HBRUSH background = PInvoke.CreateSolidBrush(Rgb(0, 120, 128));
            PInvoke.FillRect(hdc, &client, background);
            PInvoke.DeleteObject((HGDIOBJ)(nint)background.Value);

            float dpiScale = PInvoke.GetDpiForWindow(Handle) / 96f;
            RECT textRect = new()
            {
                left = client.left + (int)(16 * dpiScale),
                top = client.top + (int)(16 * dpiScale),
                right = client.right - (int)(16 * dpiScale),
                bottom = client.bottom - (int)(16 * dpiScale),
            };

            string text =
                $"Native Win32 HWND 0x{(nint)Handle.Value:X} (WS_CHILD | WS_EX_LAYERED)\n" +
                $"Painted with GDI. Tick: {_tickCount}\n" +
                $"Button clicks: {_clickCount}";

            PInvoke.SetBkMode(hdc, BACKGROUND_MODE.TRANSPARENT);
            PInvoke.SetTextColor(hdc, Rgb(255, 255, 255));
            HGDIOBJ oldFont = PInvoke.SelectObject(hdc, PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.DEFAULT_GUI_FONT));
            fixed (char* pText = text)
            {
                PInvoke.DrawText(hdc, pText, text.Length, &textRect, DRAW_TEXT_FORMAT.DT_LEFT | DRAW_TEXT_FORMAT.DT_WORDBREAK | DRAW_TEXT_FORMAT.DT_NOPREFIX);
            }
            PInvoke.SelectObject(hdc, oldFont);
        }
        finally
        {
            PInvoke.EndPaint(Handle, in ps);
        }
    }

    private static COLORREF Rgb(byte r, byte g, byte b) => (COLORREF)(uint)(r | (g << 8) | (b << 16));
}
