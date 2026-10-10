using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WindowsShell.Ui;

/// Цвета и шрифты варианта C: тёмное стекло Windows 11, Segoe UI Variable, акцент #4cc2ff.
public static class Theme
{
    public static readonly Color Accent = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    public static readonly Color Text = Color.FromArgb(255, 255, 255, 255);
    public static readonly Color TextSecondary = Color.FromArgb(0xC5, 255, 255, 255);
    public static readonly Color TextTertiary = Color.FromArgb(0x87, 255, 255, 255);
    public static readonly Color Danger = Color.FromArgb(255, 0xFF, 0x99, 0xA4);
    public static readonly Color Hover = Color.FromArgb(0x0F, 255, 255, 255);
    public static readonly Color Pressed = Color.FromArgb(0x0A, 255, 255, 255);
    public static readonly Color Stroke = Color.FromArgb(0x18, 255, 255, 255);
    public static readonly Color Separator = Color.FromArgb(0x15, 255, 255, 255);
    public static readonly Color Card = Color.FromArgb(0x0D, 255, 255, 255);
    public static readonly Color Tooltip = Color.FromArgb(0xF0, 0x2B, 0x2B, 0x2B);

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static SolidColorBrush Brush(Color c) => new(c);

    /// COLORREF для DWM (0x00BBGGRR).
    public static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));
}

/// Acrylic, который не гаснет, когда окно неактивно. Док никогда не бывает активным, а штатный
/// backdrop у неактивного окна переходит в серую заливку (это и мешало в версии на Tauri).
public sealed class AlwaysActiveAcrylic(bool dock = false) : SystemBackdrop
{
    DesktopAcrylicController? _controller;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Base,
            // Док чуть плотнее меню: иконки читаются на любом фоне.
            TintColor = dock ? Color.FromArgb(255, 0x1C, 0x1C, 0x1C) : Color.FromArgb(255, 0x2C, 0x2C, 0x2C),
            // Прозрачнее штатного (0,96): за меню и окошком звука виден размытый фон.
            TintOpacity = dock ? 0.45f : 0.2f,
            LuminosityOpacity = dock ? 0.7f : 0.72f,
            FallbackColor = Color.FromArgb(255, 0x2C, 0x2C, 0x2C),
        };
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark,
        });
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
    }
}
