# 🪟 WinUI.NativeHwndInterop

Have you ever wanted to host HWND content, WPF elements, or Windows Forms controls inside WinUI3 XAML? The WinUI framework itself only provides interop in the other direction out of the box - you can host WinUI inside of another window.

This library provides native HWND content hosting, WPF elements, and Windows Forms controls inside WinUI
XAML. Reuse existing desktop UI alongside WinUI controls, with native mouse
input, keyboard preprocessing, and Tab/Shift+Tab focus hand-off. HWNDs participate in
WinUI composition, including animations, transforms, and opacity.

There are no undocumented APIs used (just one experimental WinAppSDK API), and no overhead for hosting the HWNDs. The hosting
mechanism stitches together the HWND and XAML composition trees and normal child HWND
input processing happens automatically.

**Preview:** targets .NET 10 on Windows and requires **Windows App SDK
2.6.6-experimental** with its matching runtime. The implementation uses experimental
platform APIs and is not a platform-supported HWND hosting control. Packages are
currently built locally as `0.1.0-preview.1`; they have not been published to NuGet.org.

## ✨ Features

* 🎨 **Full visual participation in WinUI composition** - Animate, scale, rotate, and fade the hosted HWND content.
* 🖱️ **Mouse and pointer input follows the native HWND** - The hosted content receives mouse input directly, including hover, click, and drag events. HWND input doesn't participate in rotate or scale transforms, but this library does maintain the child HWND positions so that input mapping works properly. HWNDs can be optionally clipped by the framework automatically to prevent some of the airspace issues.
* 🖥️ **Mixed-mode DPI support** - The hosting control can be created in a different DPI context than the hosted HWND. The library provides opt-in awareness and scaling modes to support mixed-DPI hosting scenarios. This enables supporting old HWND-based plugins that don't support modern DPI scaling.
* ⌨️ **Keyboard input preprocessing and focus hand-off** - The library provides a keyboard bridge for WPF and Windows Forms content, allowing accelerators, mnemonics, and Tab/Shift+Tab focus traversal to work seamlessly between the hosted content and WinUI controls.
* 🔌 **Cross-process hosting for plugins** - Host WinForms or WPF UI from a cooperative plugin process, including sandboxed AppContainer processes using broker-performed parenting. The sample demonstrates the integration; composable process jobs, watchdogs, and UI heartbeats provide lifetime management and hang detection. Cross-process HWND parenting still couples input queues and is not a complete isolation boundary.

## ❓ Q&A

### When should I use this?

It's clear that hosting HWNDs is not the preferred approach for anyone. If you are building a new plugin model, you should build it with Direct3D, Composition APIs, and input redirection. However there are many existing HWND plugin models that apps are stuck with. Rather than forcing you into WinUI interop inside of an older, clankier UI framework, this library lets you build your app as WinUI-first and still adopt HWND-based plugins when it's absolutely needed.

### What is the difference between this and the WinForms/WPF + XAML Islands?

Your main window can be natively WinUI3-based, including all of the titlebar functionality. For applications where HWND content is rare or only a small portion of the UI, this library can lead to a much cleaner codebase.

### Does this really solve the airspace problem with hosting HWNDs?

Visually, yes 100%. The hosted HWND content is redirected into a DirectComposition visual and participates in the WinUI composition tree. However, the native HWND still receives mouse input directly, so if you have a rotated or scaled HWND in the XAML tree, the mouse input will not be perfectly transformed to match the visual. The library does maintain the child HWND positions so that input mapping works properly 99% of the time, but it does not provide a complete solution for all input airspace issues. Clipping of the input does not follow 100% with XAML content rules, so this library works best when your HWND content is *usually* unoccluded. It can still participate in transition animations and other temporary visual states just fine though.

## 📦 Libraries

| Library | Use it to |
| --- | --- |
| [WinUI.NativeHwndInterop](WinUI.NativeHwndInterop/README.md) | Host a native child HWND with `NativeHwndHost`, or integrate directly with composition using `INativeHwndSurface` / `NativeHwndSurface`. |
| [WinUI.NativeHwndInterop.Wpf](WinUI.NativeHwndInterop.Wpf/README.md) | Host a WPF `FrameworkElement` with `WpfHwndHost`. |
| [WinUI.NativeHwndInterop.WinForms](WinUI.NativeHwndInterop.WinForms/README.md) | Host a Windows Forms `Control` with `WinFormsHwndHost`. |
| [WinUI.NativeHwndInterop.Processes](WinUI.NativeHwndInterop.Processes/README.md) | Helper classes for cross-process UI plugins. Compose kill-on-close process jobs, dedicated-thread watchdogs, and UI-dispatched heartbeats for cooperative plugin models. |

Both framework adapters depend on the core library. The core also provides
opt-in mixed-DPI hosting scopes, child DPI-awareness selection, and automatic
redirected-surface DPI compensation. The optional Processes library is framework-neutral:
it has no WinUI, WPF, or WinForms dependency and does not provide HWND hosting or IPC.

## ⚙️ How it works

1. A real `WS_CHILD | WS_EX_LAYERED` HWND lives inside the WinUI top-level window.
   WPF uses an `HwndSource` inside a layered native container; WinForms uses a
   native container for its control.
2. DirectComposition's `IDCompositionDevice.CreateSurfaceFromHwnd` redirects that
   window's content into an `IDCompositionVisual` through `SetContent`.
3. A `Microsoft.UI.Content.ContentExternalOutputLink` exposes `IDCompositionTarget`.
   `SetRoot` connects the DirectComposition visual to the link, and its
   `PlacementVisual` is attached to the XAML composition tree using
   `ElementCompositionPreview.SetElementChildVisual`.
4. The XAML host synchronizes HWND position, size, and visual scale. By default,
   native window alpha is **1**: the original window is nearly invisible but
   receives mouse input, while the redirected visual remains opaque. A thread-local
   keyboard bridge and framework adapters handle preprocessing and focus traversal.

The pixels participate in WinUI composition; the HWND retains its native input
behavior. This does not merge the frameworks' input or accessibility trees.

## 🚀 Getting started

Start with a WinUI desktop application targeting `net10.0-windows10.0.26100.0`
and the Windows App SDK version above. Reference the adapter project you need
(or its package from your local feed). Add the corresponding desktop framework
reference to your WinUI application's `.csproj`:

```xml
<PropertyGroup>
  <PublishTrimmed>false</PublishTrimmed>
</PropertyGroup>
<ItemGroup>
  <!-- Keep only the framework(s) you use. -->
  <FrameworkReference Include="Microsoft.WindowsDesktop.App.WPF" />
  <FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />
</ItemGroup>
```

Do not enable `UseWPF` on the WinUI application: that invokes WPF's markup compiler
on WinUI XAML. Native AOT and trimming are not supported for the desktop adapters.

The examples below run in a WinUI page or window constructor after
`InitializeComponent()`. `HostPanel` is an existing WinUI `Grid` or `StackPanel`,
and `StatusText` is a WinUI `TextBlock` for displaying hosting errors.

### Host WPF

```csharp
using WinUI.NativeHwndInterop.Wpf;

var host = new WpfHwndHost
{
    Height = 200,
    Child = new System.Windows.Controls.TextBox
    {
        Text = "Hello from WPF",
        AcceptsReturn = true,
    },
};
host.HostingFailed += (_, args) => StatusText.Text = args.Exception.Message;
HostPanel.Children.Add(host);
```

### Host Windows Forms

```csharp
using WinUI.NativeHwndInterop.WinForms;

var editor = new System.Windows.Forms.TextBox
{
    Text = "Hello from Windows Forms",
    Multiline = true,
};
var host = new WinFormsHwndHost { Height = 200, Child = editor };
host.HostingFailed += (_, args) => StatusText.Text = args.Exception.Message;
HostPanel.Children.Add(host);
```

Create and use hosted content on the WinUI UI thread. Supply an unparented WPF
element or WinForms control, not a top-level window or form. You own `Child`:
the adapter detaches it on unload rather than disposing it. When permanently
finished with WinForms content, set `host.Child = null` before calling
`editor.Dispose()`.

For plain Win32 or plugin HWNDs, use `NativeHwndHost.ChildWindowFactory` and return
`NativeWindowContent`. For lifetime rules, mixed-DPI setup, and low-level
composition usage, see the [core library guide](WinUI.NativeHwndInterop/README.md).

## ⚠️ Limitations

- Framework-specific behavior is not a
  complete replacement for either framework's application or message loop.
- Native hit-testing is separate from XAML: interactive overlays and transformed
  content need an explicit input policy. Arbitrary compositor animations cannot
  be tracked reliably for native input alignment.
- Window-region clipping has a known layered-window rendering offset issue.
  DPI compensation does not fix it; `ClipMode.None` avoids regions but does not
  constrain native input to a XAML viewport.
- Automatic mouse-wheel chaining, a merged accessibility tree, and cross-framework
  drag-and-drop are not provided. Validate IME, popups, shortcuts, and third-party
  controls for your application.

See each library's guide for supported keyboard behavior and ownership details.

## 🛠️ Build and run the sample

Use Visual Studio with WinUI, WPF, and WinForms tooling and the .NET 10 SDK
(or a newer compatible SDK).

```powershell
dotnet build WinUiHwndIntegration.slnx -p:Platform=x64
dotnet run --project WinUiHwndIntegration\WinUiHwndIntegration.csproj -p:Platform=x64
```

The sample's **Library samples** page demonstrates all three hosts. The packaged
launch integration registers a development package and resolves the matching
experimental runtime.

To build a local package, run `dotnet pack` against the desired library project:

```powershell
dotnet pack WinUI.NativeHwndInterop\WinUI.NativeHwndInterop.csproj -p:Platform=x64 -o artifacts
```
