using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsShell.Ui;
using Color = Windows.UI.Color;

namespace WindowsShell.Island;

/// Кусочки стиля острова (Astra v3, эскизы WSH-E23): чёрное тело, группы #161618, строки
/// со скруглением 12, светлая основная кнопка, тёмная вторичная.
public static class IslandUi
{
    public static readonly Color Body = Color.FromArgb(255, 0x0A, 0x0A, 0x0B);
    public static readonly Color Group = Color.FromArgb(255, 0x16, 0x16, 0x18);
    public static readonly Color RowOn = Color.FromArgb(255, 0x1F, 0x1F, 0x22);
    public static readonly Color Chip = Color.FromArgb(255, 0x26, 0x26, 0x2A);
    public static readonly Color SelectedSlot = Color.FromArgb(255, 0x2C, 0x2C, 0x30);
    public static readonly Color Track = Color.FromArgb(255, 0x2C, 0x2C, 0x30);
    public static readonly Color Ink = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);
    public static readonly Color Sub = Color.FromArgb(255, 0xA1, 0xA1, 0xA8);
    public static readonly Color Faint = Color.FromArgb(255, 0x8E, 0x8E, 0x96);
    public static readonly Color Amber = Color.FromArgb(255, 0xFF, 0xB3, 0x40);
    public static readonly Color AmberText = Color.FromArgb(255, 0xFF, 0xCF, 0x85);
    public static readonly Color Green = Color.FromArgb(255, 0x7D, 0xD9, 0x9B);
    public static readonly Color Red = Color.FromArgb(255, 0xFF, 0x61, 0x59);
    public static readonly Color RedText = Color.FromArgb(255, 0xFF, 0x8A, 0x84);
    public static readonly Color Outline = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);

    public static TextBlock Text(string text, double size = 14, Color? color = null, bool bold = false, bool wrap = false) => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Theme.Brush(color ?? Ink),
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static FontIcon Glyph(string glyph, double size = 18, Color? color = null) => new()
    {
        Glyph = glyph,
        FontFamily = Theme.Icons,
        FontSize = size,
        Foreground = Theme.Brush(color ?? Ink),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// Заголовок виджета: название слева, значение справа («Звук · 64 %»).
    public static Grid Header(string title, out TextBlock right, Color? rightColor = null)
    {
        var g = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Text(title, 18, bold: true));
        right = Text("", rightColor is null ? 18 : 13, rightColor, bold: rightColor is null);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    /// Группа строк на тёмной подложке.
    public static Border GroupBox(UIElement child) => new()
    {
        Background = Theme.Brush(Group),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(4),
        Child = child,
    };

    /// Строка-кнопка во всю ширину.
    public static Button Row(UIElement content, bool on = false, string? name = null)
    {
        var b = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Theme.Brush(on ? RowOn : Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 44,
        };
        if (name != null) AutomationProperties.SetName(b, name);
        return b;
    }

    /// Основная (светлая) или вторичная (тёмная) кнопка.
    public static Button Btn(string text, bool primary = false, Action? click = null)
    {
        var t = Text(text, 13, primary ? Body : Ink, bold: primary);
        var b = new Button
        {
            Content = t,
            Background = Theme.Brush(primary ? Ink : Chip),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(14, 7, 14, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (click != null) b.Click += (_, _) => click();
        return b;
    }

    /// Ссылка-кнопка («Параметры звука Windows ↗»).
    public static Button Link(string text, Action click)
    {
        var b = new Button
        {
            Content = Text(text, 12, Sub),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// Полоска уровня (заряд, громкость): фон и заливка.
    public static Grid Bar(double fraction, Color fill, double width = 120, double height = 6)
    {
        var g = new Grid { Width = width, Height = height, CornerRadius = new CornerRadius(height / 2), Background = Theme.Brush(Track), VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(new Border
        {
            Width = Math.Clamp(fraction, 0, 1) * width,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(height / 2),
            Background = Theme.Brush(fill),
        });
        return g;
    }

    public static Slider Slider(double value) => new()
    {
        Minimum = 0,
        Maximum = 100,
        StepFrequency = 1,
        SmallChange = 2,
        LargeChange = 10,
        Value = value,
        VerticalAlignment = VerticalAlignment.Center,
        IsThumbToolTipEnabled = false,
    };

    /// Круглая кнопка со значком (mute, предыдущий / следующий).
    public static Button Round(string glyph, double size, string name, out FontIcon icon, Color? bg = null)
    {
        icon = Glyph(glyph, size * 0.4);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var b = new Button
        {
            Content = icon,
            Width = size,
            Height = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2),
            Background = Theme.Brush(bg ?? Group),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(b, name);
        return b;
    }

    /// Блок «по ширине содержимого» с переносом строк (кнопки вывода звука).
    public static VariableSizedWrapGrid Wrap() => new() { Orientation = Orientation.Horizontal, ItemHeight = 48, ItemWidth = 176 };

    public static SolidColorBrush B(Color c) => Theme.Brush(c);

    /// Элемент, который можно перетащить файлом в другое окно (StartDragAsync, урок PR #43),
    /// а короткий клик — `click`. `dropped` — файл куда-то бросили.
    public static void Draggable(UIElement element, Func<string?> path, Action click, Action? dropped = null)
    {
        var pressed = false;
        var at = new Windows.Foundation.Point();
        element.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(element);
            if (!p.Properties.IsLeftButtonPressed) return;
            pressed = true;
            at = p.Position;
            element.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        element.PointerMoved += (_, e) =>
        {
            if (!pressed) return;
            var point = e.GetCurrentPoint(element);
            var p = point.Position;
            if (Math.Abs(p.X - at.X) + Math.Abs(p.Y - at.Y) < 6) return;
            pressed = false;
            element.ReleasePointerCaptures();
            if (path() is { } file && File.Exists(file))
                Shots.FileDrag.Start(element, point, file, ok => { if (ok) dropped?.Invoke(); });
        };
        element.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            element.ReleasePointerCaptures();
            e.Handled = true;
            click();
        };
        element.PointerCaptureLost += (_, _) => pressed = false;
    }
}
