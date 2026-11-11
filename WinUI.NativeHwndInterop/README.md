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

## Opt-in mixed-DPI hosting

Enable mixed hosting **when the application creates its parent HWND**, not later
when loading the control:

```csharp
using (DpiThreadScope.MixedHosting())
{
    window = new MainWindow();
}
```

Then select creation awareness and visual compensation independently:

```csharp
var host = new NativeHwndHost
{
    ChildDpiAwareness = NativeDpiAwareness.Unaware,
    DpiScalingMode = HwndDpiScalingMode.MatchNativeWindow,
    ClipMode = HwndClipMode.None,
    ChildWindowFactory = parent => CreatePluginContent(parent),
};
```

- `ChildDpiAwareness` defaults to `NativeDpiAwareness.Inherit` (no thread override).
  Alternatives are `Unaware`, `SystemAware`, and `PerMonitorV2`. The override spans
  only `CreateContent`/`ChildWindowFactory`, not subsequent XAML/composition work.
  Changing this property recreates the content, including calling its release
  callback. A factory must create fresh content for the requested context; already
  created HWNDs are not converted. Parent prerequisites and the resulting child
  awareness are checked; failures use `HostingFailed`/`LastError`.
- `DpiScalingMode` defaults to `HwndDpiScalingMode.None`. `MatchNativeWindow`
  multiplies visual scale by parent-DPI/child-DPI and divides backing size by that
  ratio, preserving the final host extent. It updates during placement tracking
  and the regular poll even if layout is unchanged. Switching it does not recreate
  content. It also works with `Inherit` and a caller-managed awareness scope.
- `DpiThreadScope.Awareness(NativeDpiAwareness)` supports custom factories or native
  plugin initialization. `AwarenessForWindow(hwnd)` uses a live HWND's exact context
  for DPI-virtualized native queries. Scopes restore previous state, support nesting,
  require reverse-order disposal on the creating thread, and must not span `await`,
  a message loop, or unrelated XAML work. Native failures throw. No process-wide DPI
  setting or existing parent HWND is changed.
- Low-level `INativeHwndSurface.NativeScaleFactor` exposes the live parent/child
  DPI ratio without modifying `PlacementVisual`. Query after DPI changes and apply
  it in your own pixel-to-DIP scale and backing-size calculation. The surface and
  both HWNDs must remain live; query on the owning thread.

Windows' native bitmap scaling is not included in the redirected backing pixels.
At 200% monitor scaling an unaware child's raw ruler is half-sized; compensation
matches the native image and mouse coordinates. Live scale transitions worked in
the sample's manual testing. Multi-monitor transitions, fractional scales, and
complex third-party controls still need application validation. Framework adapters
inherit these properties, but framework code or pre-existing managed HWNDs can
have their own DPI policies; mixed-DPI WPF/WinForms content is not yet validated.

Clipping is **independent** and is never silently changed by a DPI option.
`ClipMode.None` is the recommended starting point for mixed-DPI experiments due
to the layered-window region bug below. It does not constrain native input to a
XAML viewport or overlay. Visual compensation does not fix that bug.

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
