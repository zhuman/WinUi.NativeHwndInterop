# Plugin process supervision

`WinUI.NativeHwndInterop.Processes` provides optional, composable lifetime and responsiveness primitives. It has no WinUI, WPF, or WinForms dependency; hosts and companions can reference it independently. It does not create HWNDs, choose IPC, or turn cross-process parenting into a hang-isolation boundary.

## ProcessJob

```csharp
using WinUI.NativeHwndInterop.Processes;

using var job = new ProcessJob();
using var process = job.StartProcess(pluginExecutable, pluginArguments);
// StartProcess creates suspended, assigns the job, then resumes. No assignment race.
```

The unnamed, non-inheritable job enables `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`; remaining members and descendants are terminated when its last handle closes (including host process exit). `Terminate(exitCode)` requests immediate termination without waiting. There are deliberately no job UI restrictions, CPU limits, or memory limits by default. Keep the job alive until after graceful shutdown; disposal is destructive to remaining members.

For custom token/AppContainer launchers, create the process with `CREATE_SUSPENDED`, call `AssignProcess(process.SafeHandle)`, then resume its primary thread. If assignment fails, terminate the suspended process and report the failure; never resume outside containment. Assignment requires appropriate process rights and can fail under incompatible existing/nested job restrictions. `AssignProcess` itself does not suspend an already running process. Disposing a `Process` wrapper does not terminate it.

## OffThreadWatchdog

```csharp
await using var watchdog = new OffThreadWatchdog(
	TimeSpan.FromSeconds(10), () => job.Terminate());
// Reset after verified progress, not merely after sending work.
watchdog.Reset();
```

A dedicated background thread checks a monotonic `Stopwatch` deadline; no UI dispatcher, synchronization context, or thread-pool timer is required to expire it. Timeout is one-shot. `Reset` returns false once stopped, expired, or already overdue. The callback must not synchronously dispatch UI work, acquire UI-held locks, or wait for plugin cooperation. Terminate first; enqueue UI notification/cleanup afterward.

`Completion` must be observed: callback exceptions fault it. `Dispose` disarms without blocking, but cannot retract a callback already committed. `DisposeAsync` also waits for completion and propagates callback failure; do not await it inside the callback. A watchdog is not a hard real-time guarantee, survives no host-process failure, and includes machine sleep/debugger pauses in its elapsed-time policy. Choose conservative deadlines appropriate to your workload.

## UI heartbeats

Host:

```csharp
await using var heartbeat = new UiHeartbeatMonitor(
	TimeSpan.FromSeconds(10), () => job.Terminate());
// Periodically, outside the UI thread:
if (heartbeat.TryCreateProbe(out long sequence))
	SendProbe(sequence); // Nonblocking caller-owned transport.
// On an authenticated reply from the companion:
bool accepted = heartbeat.Acknowledge(replySequence);
```

Companion:

```csharp
var responder = new UiHeartbeatResponder(
	tryEnqueue: callback => dispatcher.TryEnqueue(callback),
	acknowledge: sequence => QueueReply(sequence));
if (!responder.TryRespond(receivedSequence))
	throw new InvalidOperationException("UI dispatcher rejected the heartbeat.");
```

Adapt the enqueue delegate for WinForms `BeginInvoke`, WPF `Dispatcher.BeginInvoke`, or another UI loop. A reply is generated only after that UI callback executes; background pipe activity alone cannot keep the monitor alive. The monitor permits only one outstanding sequence, rejects duplicate/stale/unsolicited replies, and never resets its deadline on probe creation. Start it when the remote UI is expected to pump, and dispose it before intentional shutdown. Own the scheduling/transport cancellation and observe `Completion`. Transport peer authentication remains the caller's responsibility. A cooperative heartbeat proves dispatcher progress, not rendering progress or the absence of all deadlocks; hostile code can forge acknowledgements.

Use a separate startup watchdog while HWND attachment is pending. UI heartbeat monitoring cannot protect earlier blocking attachment unless it is armed before that operation. For recovery when even the host process cannot schedule, use a separate supervisor process.
