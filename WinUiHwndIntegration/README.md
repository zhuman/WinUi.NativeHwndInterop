# Sample application

The navigation pages demonstrate native Win32, WPF, WinForms, mixed-DPI hosting, and an experimental cross-process child HWND.

## Cross-process experiment

Build and run the sample normally, then select **Cross-process**. It starts automatically; **Start / restart plugin** and **Stop plugin** control its lifetime. The helper is another instance of the same deployed executable, not another WinUI window. No separate executable copy or package deployment step is required.

The host launches it with:

```text
WinUiHwndIntegration.exe --external-child <container HWND in decimal> <host PID> <random pipe name>
```

This mode is an internal sample protocol, not a supported plugin API. The HWND and pipe must belong to a live, cooperating host.

### Window and composition arrangement

```text
WinUI top-level HWND                         host process
  layered WS_CHILD container                 host process, WinUI thread
	WS_CHILD WinForms ContainerControl       companion process, separate UI thread
	  text boxes / button / ticking label    companion process
```

The container is returned by a sample `INativeHwndContent` implementation. The existing library redirects **the local container and its descendant pixels** using `CreateSurfaceFromHwnd`, a DirectComposition visual, and `ContentExternalOutputLink`. This does not pass a foreign-process HWND directly to `NativeHwndSurface`; the libraries' same-thread validation remains unchanged.

A duplex, asynchronous, current-user-only named pipe carries the ready HWND/PID, physical sizes, focus entry, Tab departure and shutdown. Both ends verify the pipe peer's PID; the host checks the returned HWND's owner and direct parent. The companion creates its HWND directly with `WS_CHILD`, rather than reparenting a top-level window. It runs its own WinForms message loop and processes its own input. Host UI code does not wait synchronously for remote window creation or IPC.

### Trying it

- Type in **Before plugin**, then Tab through the remote text box, button, final text box and **After plugin**. Shift+Tab traverses in reverse.
- Click the remote button or press Space on it; its counter belongs to the companion process.
- Compare **Redirected + input**, **Native only**, and **Redirected only (cloaked)**. The timer should continue updating even in the cloaked mode, proving that the visible image is redirected, not the native source window.
- Resize the WinUI window; the companion receives the container's physical dimensions.
- Stop/restart or navigate away and back. Cleanup closes the pipe, requests exit, and terminates only the owned companion if it does not exit within the shutdown timeout. Container destruction is deferred until the remote process has exited so the WinUI thread does not wait for cooperative remote destruction.
- Terminate the companion in Task Manager. The page reports the broken connection; **Start / restart plugin** recovers. The companion also watches the host process so it exits if the host disappears.

### Boundaries

- **Trusted, cooperative processes only.** HWND parenting can implicitly attach input queues. Asynchronous IPC does not make cross-process `CreateWindowEx`, focus, layout or destruction immune to a hung remote UI thread. This is not a sandbox or a general fault-isolation boundary.
- Both processes must run at compatible integrity levels, in the same interactive session/desktop. This sample requires a PerMonitorV2 parent and uses PerMonitorV2 in the companion; it does not combine the mixed-DPI and cross-process experiments. Windows can reset a process's DPI awareness when parenting across incompatible contexts ([Microsoft guidance](https://learn.microsoft.com/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows#common-pitfalls-win32)).
- Clipping regions are disabled here to isolate redirection from the known layered-window region drawing issue. Native input can extend beyond a scrolled/clipped XAML host. Keep the host fully visible when comparing input. This sample does not provide production clipping policy.
- Focus and Tab are cooperative. The host's local keyboard hook cannot preprocess the companion's message queue. No general cross-process accelerator, IME, accessibility, wheel-routing, drag-and-drop or modality integration is promised.
- Normal runtime startup is used in the companion; WinUI/composition engine initialization is skipped. System-composition compatibility remains unverified.

On the development machine (Windows 11, x64 process on ARM64, 200% scaling), verification confirmed foreign HWND ownership/parentage, opaque live redirected pixels while cloaked, typing, real mouse clicks, forward/reverse Tab, resizing, exit-code-zero shutdown, navigation cleanup, and restart after unexpected companion exit.
