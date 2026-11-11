using System.Runtime.InteropServices;
using Microsoft.UI.Composition;

namespace WinUI.NativeHwndInterop;

/// <summary>A live, redirected HWND surface. This object does not own the source HWND.</summary>
public interface INativeHwndSurface : IDisposable
{
    nint WindowHandle { get; }
    Visual PlacementVisual { get; }
    /// <summary>Current parent-DPI/child-DPI ratio from backing pixels to native displayed pixels.
    /// Query again after DPI changes. Does not modify PlacementVisual.</summary>
    double NativeScaleFactor { get; }
}

/// <summary>Keyboard messages are offered before TranslateMessage/DispatchMessage on the hosting UI thread.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeMessage
{
    public nint Window;
    public uint Id;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public int X;
    public int Y;
}

public sealed class NativeFocusNavigationEventArgs(bool forward) : EventArgs
{
    public bool Forward { get; } = forward;
    public bool Handled { get; set; }
}

/// <summary>Framework-specific lifetime, keyboard preprocessing and tab-boundary behavior.</summary>
/// <remarks>All calls occur on the WinUI UI thread. Disposing releases the adapter's host HWND
/// but need not dispose caller-supplied managed content. Borrowed HWND wrappers must document
/// caller ownership. Raise FocusNavigationRequested only after native traversal has exhausted
/// the subtree, not for every Tab key.</remarks>
public interface INativeHwndContent : IDisposable
{
    nint WindowHandle { get; }
    bool Focus(bool forward);
    bool TranslateMessage(ref NativeMessage message);
    event EventHandler<NativeFocusNavigationEventArgs>? FocusNavigationRequested;
}

public sealed class NativeHwndErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
