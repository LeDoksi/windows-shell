using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Ui;

namespace WindowsShell.Tail;

/// Общие кусочки окошек хвоста (сеть, батарея, часы) в духе окошек Windows 11.
public static class FlyoutUi
{
    public static TextBlock Label(string text, double size = 14, bool secondary = false, bool bold = false) => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Theme.Brush(secondary ? Theme.TextSecondary : Theme.Text),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static FontIcon Icon(string glyph, double size = 16, bool accent = false) => new()
    {
        Glyph = glyph,
        FontFamily = Theme.Icons,
        FontSize = size,
        Foreground = Theme.Brush(accent ? Theme.Accent : Theme.Text),
    };

    /// Строка-кнопка: значок, название, пояснение под ним и что-то справа.
    public static Button Row(string glyph, string title, string? subtitle, UIElement? right = null, bool selected = false, bool accentIcon = false)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Icon(glyph, 18, accentIcon);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(Label(title));
        if (!string.IsNullOrEmpty(subtitle)) text.Children.Add(Label(subtitle, 12, secondary: true));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (right != null)
        {
            Grid.SetColumn((FrameworkElement)right, 2);
            grid.Children.Add(right);
        }
        return new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Theme.Brush(selected ? Theme.Selected : Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            MinHeight = 48,
            Padding = new Thickness(12, 6, 10, 6),
        };
    }

    /// Ссылка внизу окошка («Параметры сети»).
    public static HyperlinkButton Link(string text, Action click)
    {
        var b = new HyperlinkButton { Content = Label(text, 13), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(4), Height = 32 };
        b.Click += (_, _) => click();
        return b;
    }

    public static Border Separator() => new() { Height = 1, Background = Theme.Brush(Theme.Separator), Margin = new Thickness(-16, 8, -16, 8) };
}
