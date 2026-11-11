# WinUI native HWND embedding experiment

Three packable libraries and a WinUI sample:

| Project / future package | Public entry points |
| --- | --- |
| WinUI.NativeHwndInterop | INativeHwndSurface, NativeHwndSurface, NativeHwndHost, NativeWindowContent |
| WinUI.NativeHwndInterop.Wpf | WpfHwndHost |
| WinUI.NativeHwndInterop.WinForms | WinFormsHwndHost |

Names use **WinUI**, without a version suffix. All packages are version
`0.1.0-preview.1`; nothing is published automatically.

## Build and run

Use Visual Studio with WinUI, WPF and WinForms tooling and the .NET 10 SDK (or a
newer compatible SDK). The sample is packaged; its `dotnet run` integration
registers a development package and resolves the matching experimental runtime.

```powershell
dotnet build WinUiHwndIntegration.slnx -p:Platform=x64
dotnet run --project WinUiHwndIntegration\WinUiHwndIntegration.csproj -p:Platform=x64
```

Home retains the original alpha/cloak/region/animation experiments, now using the
library control. **Library samples** demonstrates native Win32, WinForms and WPF
hosts, each between two XAML text boxes for testing Tab/Shift+Tab. Navigate away
and back to exercise destruction and recreation.

On Windows x64, runtime checks cover forward/reverse tab traversal for all three
hosts, text input and keyboard button activation without duplication in WPF and
WinForms, real mouse clicks at alpha 1, opaque redirected framework rendering,
and repeated navigation/unload/recreation without leftover framework HWNDs.
Other architectures and complex third-party controls have not been runtime-tested.

WPF uses a separate layered native container: an opaque `HwndSource` actively
removes `WS_EX_LAYERED` from its own HWND. Native hosts must stay above the XAML
island's sibling HWND for input and reliable framework rendering.

## Create packages locally

```powershell
dotnet pack WinUI.NativeHwndInterop\WinUI.NativeHwndInterop.csproj -p:Platform=x64 -o artifacts
dotnet pack WinUI.NativeHwndInterop.Wpf\WinUI.NativeHwndInterop.Wpf.csproj -p:Platform=x64 -o artifacts
dotnet pack WinUI.NativeHwndInterop.WinForms\WinUI.NativeHwndInterop.WinForms.csproj -p:Platform=x64 -o artifacts
```

The adapter packages depend on the base package. Before publication, choose package
ownership, license, repository metadata and a support/versioning policy. No license
or authorship claim is inferred by this prototype.

## Important constraints

This research depends on **Windows App SDK 2.6.6-experimental** because
`ContentExternalOutputLink` is experimental. It is not a generally supported
WinUI HWND embedding API. The package readmes document lifetime, ownership,
keyboard handling and remaining limitations. WPF/WinForms consumers must disable
trimming; the sample does so in both Debug and Release. Native AOT is not supported
for the desktop framework adapters.

Alpha 1 hides almost all native rendering while preserving native mouse input;
the redirected composition content remains opaque. `SetWindowRgn` can constrain
hit-testing, but its observed drawing offset bug also affects redirected content.
Compositor-only transformations, interactive overlays, wheel chaining, merged
accessibility, IME/popups and complex framework integration need further validation.
