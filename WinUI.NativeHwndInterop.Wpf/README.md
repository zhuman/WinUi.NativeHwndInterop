# WinUI.NativeHwndInterop.Wpf

Hosts a WPF `FrameworkElement` in a WinUI layout via `WinUI.NativeHwndInterop`.

```csharp
var host = new WpfHwndHost { Child = new System.Windows.Controls.TextBox() };
```

- A layered native container is the direct child of the WinUI backing HWND. WPF renders normally in an opaque, non-layered child `HwndSource`; the container resizes it to fill its client area.
- You own `Child`; the host never disposes it. It is detached from the `HwndSource` on unload or change.
- The element must belong to the hosting UI thread, have no logical/visual parent or existing presentation source, and must not be a WPF `Window`.
- Keyboard: the `IKeyboardInputSink` sequence (accelerators, chars, mnemonics) is driven manually, since a child `HwndSource` does not register with `ComponentDispatcher`.
- Tab/Shift+Tab leaving the WPF tab order (`OnNoMoreTabStops`) returns focus to XAML.
- Not supported: a WPF `Application`/resources, arrow-key focus hand-off, wheel chaining, per-pixel transparency.
- UI thread only.

The inherited core `ChildDpiAwareness` and `DpiScalingMode` options are opt-in.
See the core README for parent mixed-hosting prerequisites and clipping limits.
These options do not override WPF's own DPI/layout policy; non-default awareness
with WPF content requires separate validation.
