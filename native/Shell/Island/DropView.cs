using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island;

/// Режим приёма файлов (П2, эскиз Shelf): две цели — «Корзина» (удалить, как бросок на
/// корзину в доке) и «Полка» (отложить ссылку). Цель под курсором подсвечена.
public sealed class DropView
{
    public const double W = 480, H = 186;
    static readonly Color Dash = Color.FromArgb(255, 0x3A, 0x3A, 0x3F);
    static readonly Color AccentBg = Color.FromArgb(255, 0x10, 0x22, 0x2C);
    static readonly Color Accent = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    static readonly Color DangerBg = Color.FromArgb(255, 0x2A, 0x14, 0x14);

    readonly Border _bin, _shelf;
    readonly FontIcon _binIcon, _shelfIcon;
    DropTarget _hover;

    public Grid View { get; } = new() { Width = W, Height = H, Padding = new Thickness(18), ColumnSpacing = 12, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

    public DropView()
    {
        View.ColumnDefinitions.Add(new ColumnDefinition());
        View.ColumnDefinitions.Add(new ColumnDefinition());
        _bin = Target("", "Корзина", "удалить", out _binIcon);
        _shelf = Target("", "Полка", "отложить ссылку", out _shelfIcon);
        Grid.SetColumn(_shelf, 1);
        View.Children.Add(_bin);
        View.Children.Add(_shelf);
        Hover(DropTarget.None);
    }

    static Border Target(string glyph, string title, string sub, out FontIcon icon)
    {
        icon = Glyph(glyph, 28);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        var t = Text(title, 15, bold: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(t);
        var s = Text(sub, 12, Sub);
        s.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(s);
        return new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1.5), Child = stack };
    }

    /// Какая цель под точкой (координаты View).
    public DropTarget HitTest(Windows.Foundation.Point p)
    {
        if (p.Y < 0 || p.Y > H + 40 || p.X < -40 || p.X > W + 40) return DropTarget.None;
        return p.X < W / 2 ? DropTarget.Bin : DropTarget.Shelf;
    }

    public void Hover(DropTarget t)
    {
        _hover = t;
        _bin.BorderBrush = B(t == DropTarget.Bin ? Red : Dash);
        _bin.Background = B(t == DropTarget.Bin ? DangerBg : Color.FromArgb(0, 0, 0, 0));
        _binIcon.Foreground = B(t == DropTarget.Bin ? RedText : Ink);
        _shelf.BorderBrush = B(t == DropTarget.Shelf ? Accent : Dash);
        _shelf.Background = B(t == DropTarget.Shelf ? AccentBg : Color.FromArgb(0, 0, 0, 0));
        _shelfIcon.Foreground = B(t == DropTarget.Shelf ? Accent : Ink);
    }

    public DropTarget Current => _hover;
}
