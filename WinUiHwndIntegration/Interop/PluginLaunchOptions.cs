namespace WinUiHwndIntegration.Interop;

internal enum PluginAttachment { Direct, Broker }
internal enum PluginIsolation { None, AppContainer }
internal enum PluginDpiAwareness { PerMonitorV2, Unaware, SystemAware }
internal enum PluginFramework { WinForms, Wpf }

internal readonly record struct PluginLaunchOptions(
    PluginAttachment Attachment,
    PluginIsolation Isolation,
    PluginDpiAwareness DpiAwareness,
    PluginFramework Framework);
