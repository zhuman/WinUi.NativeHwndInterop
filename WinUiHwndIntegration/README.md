# Sample application

The navigation pages demonstrate native Win32, WPF, WinForms, mixed-DPI hosting, and an experimental cross-process child HWND. The experiments described here are sample-only; they do not change the reusable libraries' defaults or same-thread HWND validation.

## Cross-process experiment

Build and run the sample normally, then select **Cross-process**. It starts automatically; **Start / restart plugin** and **Stop plugin** control its lifetime. Choose framework, attachment, isolation, and DPI options, then restart to apply them.

The solution builds a standalone desktop companion, `WinUiHwndIntegration.Plugin`, and deploys its output into the sample's `Plugin` subdirectory. **Plugin framework** selects WinForms (the default) or WPF content. Both offer editable text boxes, a counter button, and a live render-tick label, all owned and rendered by the companion process. It does not initialize WinUI. Its internal command line is:

```text
WinUiHwndIntegration.Plugin.exe <container HWND in decimal> <host PID> <random pipe name> <Direct|Broker> <PerMonitorV2|Unaware|SystemAware> <WinForms|Wpf>
```

This is not a supported plugin protocol. The HWND and pipe must belong to a live, cooperating host.

### Window and composition arrangement

```text
WinUI top-level HWND                         host process, PerMonitorV2 + MIXED hosting
  layered WS_CHILD container                 host process, WinUI thread, selected DPI awareness
	WS_CHILD WinForms ContainerControl       companion process, matching DPI awareness
	  text boxes / button / ticking label    companion process
```

The container is returned by a sample `INativeHwndContent` implementation. The existing library redirects **the local container and its descendant pixels** using `CreateSurfaceFromHwnd`, a DirectComposition visual, and `ContentExternalOutputLink`. No foreign-process HWND is passed directly to `NativeHwndSurface`.

In WPF mode the remote `ContainerControl` contains an `ElementHost` and WPF `HwndSource`/visual tree instead of WinForms controls. This deliberately reuses the companion's HWND, message loop, and IPC lifetime plumbing; it is not an in-process WPF host or a separate WPF `Application`. Focus entry and basic Tab departure are cooperative; the richer in-process adapter integration is not replicated here.

- **Direct child creation:** the companion creates its `WS_CHILD` directly beneath the supplied container.
- **Broker SetParent:** the companion creates its child beneath an invisible, own-process parking window. The host verifies the pipe peer PID, child ownership, `WS_CHILD` style, parking-parent ownership, and matching DPI awareness before calling `SetParent` on the WinUI thread. It checks the resulting parent/DPI and acknowledges attachment before the companion shows its UI. HWND ownership remains with the companion.

Both ends verify the pipe peer PID. A duplex asynchronous pipe carries the ready HWND/PID, attachment acknowledgement, container-local sizes, focus entry, Tab departure, startup errors, UI heartbeats, and shutdown. The companion runs its own WinForms message loop and processes its own input. The host's session/IPC work runs outside the WinUI synchronization context; native HWND operations can still couple the processes.

### Process lifetime and hang detection

The sample composes the framework-neutral [Processes library](../WinUI.NativeHwndInterop.Processes/README.md):

- **`ProcessJob`** owns a kill-on-close Windows job. Both normal and AppContainer launches create the companion suspended, assign it to the job, then resume it. Assignment failure aborts launch; there is no uncontained fallback. Closing the host or terminating the job also terminates companion descendants that remain in the job.
- **`OffThreadWatchdog`** uses its own background thread and a monotonic deadline. A 15-second startup/attachment watchdog and a 4-second shutdown watchdog can terminate the job without dispatching to WinUI. The shutdown path normally allows 2 seconds for cooperative exit before cleanup terminates an unresponsive process.
- **`UiHeartbeatMonitor` / `UiHeartbeatResponder`** correlate one outstanding probe at a time. Probes run every second; replies are queued through the companion's actual UI dispatcher, not acknowledged by its IPC thread. Five seconds without a valid UI reply triggers job termination and a visible failure diagnosis. Probe transmission itself never refreshes the deadline.

**Hang plugin UI (watchdog demo)** deliberately freezes the remote UI while leaving its IPC thread alive. The watchdog terminates the job and the page permits restarting. Intentional shutdown disarms runtime heartbeats; watchdog completion is observed before releasing the job.

These are composable lifetime/liveness tools, not guaranteed hang isolation. Cross-process child HWNDs may share input queues, and native calls can still block the host UI. Timeouts count sleep/debug pauses; scheduler starvation can delay termination. A cooperative heartbeat is not proof against a malicious plugin. Native process creation itself is not bounded by the post-launch startup watchdog; use a separate supervisor process if the host must remain responsive under all circumstances.

### AppContainer option

**AppContainer (none requested)** creates a unique temporary profile and launches the companion using `SECURITY_CAPABILITIES` with an empty requested capability list. The host checks the actual token's AppContainer flag, exact package SID, and low integrity level; token capability SID count is also shown. Windows supplied one additional capability SID on the test machine despite the empty requested list, so this is not a claim that the effective token contains zero SIDs.

Only the helper payload is staged into the profile, with read/execute access granted to that exact AppContainer SID. The repository and installed runtime ACLs are unchanged. The random pipe has a restricted DACL for the broker user, SYSTEM, and exact package SID, plus a low mandatory label. There is no unrestricted-process fallback, broad `ALL APPLICATION PACKAGES` grant, or capability request for networking or user libraries.

Use **Broker SetParent** with this option. On the tested system, direct child creation under the higher-integrity host fails with `ERROR_ACCESS_DENIED`; broker attachment succeeds. **No `ChangeWindowMessageFilterEx` exceptions, global UIPI relaxation, or synthetic input relay were needed** for the verified mouse, typing, and Tab scenarios. Other controls/messages may need separate investigation; any future exception should be justified per message and applied only to the local container.

Normal stop, navigation away, startup cancellation, and application close wait asynchronously for the owned process to exit before deleting its staged payload/profile. An unresponsive owned process is terminated after a bounded timeout. Pipe loss makes the companion exit even if its UI thread is unresponsive. Abrupt termination of the host can leave a temporary AppContainer profile/payload; this sample has no crash-recovery scavenger.

### Mixed DPI

The local container and companion both use the selected **PerMonitorV2**, **Unaware (96 DPI)**, or **System aware** mode. Mixed hosting occurs between the PerMonitorV2 WinUI top-level and the legacy container/subtree; the **cross-process parent/child edge deliberately has matching awareness**. The host checks awareness before and after broker attachment to avoid Windows' cross-process DPI-reset behavior ([Microsoft guidance](https://learn.microsoft.com/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows#common-pitfalls-win32)).

The page opts into `ChildDpiAwareness` and `HwndDpiScalingMode.MatchNativeWindow`. Resize messages use the container's own backing-pixel dimensions, not the WinUI host's physical dimensions. At 200% scaling, unaware windows remain at 96 DPI and redirected pixels use a 2.00 correction factor; PerMonitorV2/system-aware windows report 192 DPI with a 1.00 factor.

### Trying it

- Select **WinForms** or **WPF** under **Plugin framework**, then restart. The companion's heading and connection status identify which framework is running.
- Type in **Before plugin**, then Tab through the remote text box, button, final text box and **After plugin**. Shift+Tab traverses in reverse.
- Click the remote button or press Space on it; its counter belongs to the companion process.
- Compare **Redirected + input**, **Native only**, and **Redirected only (cloaked)**. The timer should keep updating while cloaked, proving that the visible image is redirected.
- Resize the WinUI window; verify the companion follows it without double-scaling.
- Stop/restart, navigate away/back, or terminate the companion in Task Manager. Unexpected exit is reported on the page; restarting should recover.
- Click **Hang plugin UI (watchdog demo)**. The heartbeat failure should terminate the companion in approximately five seconds; restart to recover.
- Change display scaling manually to investigate live DPI transitions. Those transitions have not yet been verified for these cross-process combinations.

### Measured results

On Windows 11, using an x64 process on ARM64 at 200% scaling, the following results apply to **both WinForms and WPF**:

| Isolation | Attachment | PerMonitorV2 | Unaware | System aware |
|---|---|---|---|---|
| Normal token | Direct | Pass | Pass | Pass |
| Normal token | Broker | Pass | Pass | Pass |
| AppContainer | Direct | Access denied | Access denied | Access denied |
| AppContainer | Broker | Pass | Pass | Pass |

Each passing combination verified foreign HWND ownership/parentage, matching DPI at the cross-process edge, typing exactly once, keyboard/button clicks, real mouse input, bidirectional Tab, visual/native button-edge alignment in both native and cloaked-redirection modes, changing screenshot pixels while cloaked, and exit-code-zero shutdown. Additional checks covered unaware AppContainer resizing, unexpected exit/restart, repeated navigation, rapid startup cancellation, actual profile/payload removal, and normal app-close cleanup. Existing Win32/WPF/WinForms and in-process mixed-DPI input checks also passed. WPF input verification used normal-duration key presses; zero-delay injected key sequences do not have the in-process adapter's keyboard preprocessing guarantees.

Supervision verification covered healthy UI beyond the timeout and deliberate hangs for WinForms/WPF with normal tokens and brokered AppContainer, using unaware DPI. The job exited with the watchdog's `0xDEAD` code in approximately five seconds, reported a UI heartbeat timeout, and supported restart/graceful stop. A hung WPF AppContainer was also terminated while the host UI thread was temporarily suspended; resuming the host restored diagnostics and restart. Component checks verified descendant termination, kill-on-close after abrupt broker exit, concurrent reset/disposal, rejected stale/late replies, and observable callback failures. The full framework/DPI matrix and sandbox lifecycle checks were rerun with supervision enabled; no companion processes or temporary profiles remained.

### Boundaries

- **Cooperative UI only.** AppContainer provides resource restrictions, not a fully audited plugin sandbox or hang-isolation boundary. Cross-process parenting can implicitly attach input queues; async IPC does not make focus, layout, parenting, or destruction immune to a hung/malicious remote UI thread. The broker and protocol are not hardened against hostile plugins.
- Both processes must use the same interactive session/desktop. Only the displayed modes and basic controls have been exercised; do not generalize the result to arbitrary integrity levels or mismatched cross-process DPI contexts.
- Clipping regions are disabled to isolate redirection from the known layered-window region drawing issue. Native input can extend beyond a scrolled/clipped XAML host; keep it fully visible while testing.
- Focus and Tab are cooperative. The local keyboard hook cannot preprocess the companion's queue. No general cross-process accelerator, IME, accessibility, wheel-routing, drag-and-drop, or modality integration is promised.
- System-composition compatibility remains unverified.
