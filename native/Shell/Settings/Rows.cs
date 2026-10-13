using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Ui;

namespace WindowsShell.Settings;

/// Строки окна настроек в духе «Параметров» Windows 11: карточки, в карточке строки
/// «название и пояснение — переключатель (или другой элемент) справа».
public static class Rows
{
    public static TextBlock Text(string text, double size = 14, bool secondary = false, bool bold = false) => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Theme.Brush(secondary ? Theme.TextSecondary : Theme.Text),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// Заголовок страницы.
    public static TextBlock Title(string text)
    {
        var t = Text(text, 28, bold: true);
        t.Margin = new Thickness(0, 0, 0, 12);
        return t;
    }

    /// Подзаголовок над карточкой.
    public static TextBlock Section(string text)
    {
        var t = Text(text, 14, bold: true);
        t.Margin = new Thickness(0, 18, 0, 6);
        return t;
    }

    /// Серое пояснение под карточкой.
    public static TextBlock Note(string text)
    {
        var t = Text(text, 12, secondary: true);
        t.Margin = new Thickness(2, 6, 0, 0);
        return t;
    }

    /// Карточка: строки с тонкими разделителями.
    public static Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Theme.Brush(Theme.Separator), Margin = new Thickness(-16, 0, -16, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Background = Theme.Brush(Theme.Card),
            BorderBrush = Theme.Brush(Theme.Stroke),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 2, 16, 2),
            Margin = new Thickness(0, 0, 0, 4),
            Child = stack,
        };
    }

    /// Строка «название, пояснение — элемент справа».
    public static Grid Row(string title, string? hint, UIElement? right, double rightWidth = double.NaN)
    {
        var row = new Grid { ColumnSpacing = 16, MinHeight = 60, Padding = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(rightWidth) ? GridLength.Auto : new GridLength(rightWidth) });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { Text(title) } };
        if (hint != null) text.Children.Add(Text(hint, 12, secondary: true));
        row.Children.Add(text);
        if (right is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 1);
            row.Children.Add(fe);
        }
        return row;
    }

    public static Grid Switch(string title, string? hint, bool on, Action<bool> changed) =>
        Row(title, hint, Toggle(on, changed));

    /// Переключатель; включённый — нашим акцентом, как в Windows 11 (иначе серый и не видно, что включён).
    public static ToggleSwitch Toggle(bool on, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        foreach (var key in new[] { "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed" })
            toggle.Resources[key] = Theme.Brush(Theme.Accent);
        foreach (var key in new[] { "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed" })
            toggle.Resources[key] = Theme.Brush(Microsoft.UI.Colors.Black);
        toggle.Resources["ToggleSwitchStrokeOn"] = Theme.Brush(Theme.Accent);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    /// Флажок нашим акцентом.
    public static CheckBox Check(string text, bool on, Action<bool> changed)
    {
        var box = new CheckBox { Content = Text(text), IsChecked = on, MinHeight = 40 };
        foreach (var key in new[] { "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
                     "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed" })
            box.Resources[key] = Theme.Brush(Theme.Accent);
        foreach (var key in new[] { "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed" })
            box.Resources[key] = Theme.Brush(Microsoft.UI.Colors.Black);
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    /// Выбор одного из нескольких: кружки нашим акцентом.
    public static StackPanel Choice(IEnumerable<(string Title, bool Selected, Action Pick)> options)
    {
        var panel = new StackPanel { Padding = new Thickness(0, 6, 0, 6) };
        var group = Guid.NewGuid().ToString();
        foreach (var (title, selected, pick) in options)
        {
            var radio = new RadioButton { Content = Text(title), GroupName = group, IsChecked = selected, MinHeight = 40 };
            foreach (var key in new[] { "RadioButtonOuterEllipseCheckedFill", "RadioButtonOuterEllipseCheckedFillPointerOver", "RadioButtonOuterEllipseCheckedFillPressed",
                         "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedStrokePointerOver", "RadioButtonOuterEllipseCheckedStrokePressed" })
                radio.Resources[key] = Theme.Brush(Theme.Accent);
            foreach (var key in new[] { "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed" })
                radio.Resources[key] = Theme.Brush(Microsoft.UI.Colors.Black);
            var p = pick;
            radio.Checked += (_, _) => p();
            panel.Children.Add(radio);
        }
        return panel;
    }

    public static Button Button(string text, Action click)
    {
        var b = new Button { Content = Text(text, 13), MinWidth = 120 };
        b.Click += (_, _) => click();
        return b;
    }
}
