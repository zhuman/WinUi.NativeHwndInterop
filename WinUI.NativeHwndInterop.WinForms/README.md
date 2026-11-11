# WinUI.NativeHwndInterop.WinForms

Hosts a Windows Forms control in a WinUI layout via `WinUI.NativeHwndInterop`.

```csharp
var host = new WinFormsHwndHost { Child = new System.Windows.Forms.TextBox() };
```

- You own `Child`; the host never disposes it. It is detached (and its `Dock` restored) on unload or change.
- The control must have no parent, not be a top-level `Form`, and must be created/used on the hosting UI thread. Existing handles on another thread are rejected; controls without handles cannot reliably report thread ownership.
- A control cannot be assigned to two hosts, including while unloaded. Clear the first host's `Child` before transferring it.
- Keyboard: messages go through `Control.PreProcessControlMessage` (command/dialog keys, modifiers, multiline Tab, mnemonics).
- Tab/Shift+Tab stop at the first/last tab stop (no wrap) and return focus to XAML.
- Not supported: `Application.Idle`/message filters, wheel chaining, Form features (AcceptButton, menus), per-monitor DPI re-scaling.
- UI thread only.
