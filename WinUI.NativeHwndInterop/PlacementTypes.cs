namespace WinUI.NativeHwndInterop;

public enum HwndCloakMode
{
    /// <summary>Enable native input only while observed layout is static, fully visible and representable.</summary>
    Auto,
    AlwaysCloaked,
    NeverCloaked,
}

/// <remarks>A window region also clips redirected content. On some Windows versions layered child
/// windows exhibit a DWM region-offset rendering bug; these modes are diagnostic, not general XAML clipping.</remarks>
public enum HwndClipMode
{
    None,
    VisibleArea,
    DebugLeftHalf,
}

/// <summary>A rectangle in physical pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    internal int Right => checked(Left + Width);
    internal int Bottom => checked(Top + Height);
}

/// <param name="Bounds">HWND bounds relative to its top-level parent's client area, in physical pixels.</param>
/// <param name="Visible">The visible portion after observed XAML rectangular clipping.</param>
/// <param name="Scale">The XamlRoot rasterization scale.</param>
/// <param name="UnrepresentableReason">Why native input cannot match the observed XAML geometry, or null.</param>
public readonly record struct HwndPlacement(PixelRect Bounds, PixelRect Visible, double Scale, string? UnrepresentableReason);
