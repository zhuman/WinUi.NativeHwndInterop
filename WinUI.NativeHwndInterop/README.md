# WinUI.NativeHwndInterop

Experimental same-thread HWND embedding for WinUI, targeting .NET 10 on Windows.
Requires Windows App SDK **2.6.6-experimental** and its matching experimental runtime.
This package is not a supported replacement for a platform HWND-hosting control.

## Two layers

- `INativeHwndSurface` / `NativeHwndSurface`: wraps a live layered HWND using
  `CreateSurfaceFromHwnd`, a DirectComposition visual, and `ContentExternalOutputLink`.
  The constructor takes the HWND and a WinUI `Compositor`. Parent `PlacementVisual`
  into your composition tree, set its size and pixel-to-DIP scale, and dispose the
  surface before destroying the HWND. The surface does not own or position the HWND.
- `NativeHwndHost`: a focusable XAML `Control` that owns an `INativeHwndContent`
  returned by `ChildWindowFactory`. The factory receives the top-level parent HWND.
  Create a **new same-thread WS_CHILD** window each time the factory is called.
  The control enables `WS_EX_LAYERED`, synchronizes placement/DPI, and releases its
  content on unload. Reloading creates fresh content.

For plain Win32 content, return `NativeWindowContent(hwnd, releaseCallback)` from
that factory. The callback must release the window and any associated resources.
Without a callback, `NativeWindowContent` borrows the HWND; the caller must keep it
alive until the host unloads and must not reuse it concurrently in another host.
Use the WPF and WinForms adapter packages for those frameworks' keyboard handling.

## Input and visual policy

Defaults are `NativeAlpha = 1`, `CloakMode = NeverCloaked`, and
`ClipMode = VisibleArea`. Experiments showed that layered-window constant alpha
changes the real HWND but not its redirected visual. Alpha 1 is nearly invisible
and remains mouse-active; alpha 0 is mouse-transparent. Opacity on the redirected
visual is a separate concern.

`SetWindowRgn` restricts native hit-testing. On our test system (Windows 11 at
200% scale), its rendering clip was shifted by the top-level client origin in
screen coordinates, including in the redirected surface. Therefore it is not a
reliable visual clipping API. Visible-area regions are removed while cloaked;
XAML handles visual clipping. DebugLeftHalf deliberately exposes this behavior.

Layout events and a 100 ms poll track placement. While motion is detected,
per-frame tracking is enabled. Auto cloaking hides the HWND during detected
motion, unsupported transforms/opacity, and partial clipping. Independent
composition animations cannot be reliably read back; polling does not guarantee
frame-perfect native hit-testing. NeverCloaked is best suited to static,
axis-aligned content and ordinary scrolling. Rotated/scaled visuals do not imply
rotated/scaled HWND input.

The host is one XAML tab stop. A per-thread native message hook preprocesses
keyboard messages only for registered hosted HWND subtrees. Adapter implementations
perform native traversal, then request departure to the next/previous XAML element.
No global hook or secondary message loop is installed. `NativeWindowContent`
provides basic dialog-style tab navigation, not a full native dialog manager.

Mouse capture and clicks remain native. XAML overlays do not automatically win
hit-testing over an HWND: provide appropriate native input regions if interactive
overlays must receive input. Wheel messages do not automatically bubble through
XAML ancestry. Automatic wheel chaining, touch/pen synthesis, drag-and-drop, merged
UI Automation trees, global accelerator integration, and cross-thread/cross-process
hosting are not provided. IME and popup behavior require application testing.

Handle `HostingFailed` to display creation/input errors; otherwise they surface as
exceptions. `LastError`, `StateChanged`, and placement/region properties support
diagnostics. All host and surface operations must stay on the owning UI thread.

The public API is preview and may change before a stable package is published.
