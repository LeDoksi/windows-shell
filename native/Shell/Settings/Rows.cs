using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Ui;

namespace WindowsShell.Settings;

/// Строки окна настроек в духе «Параметров» Windows 11: карточки, в карточке строки
/// «название и пояснение — переключатель (или другой элемент) справа».
public static class Rows
{
    /// Строим лист настроек острова (П14): те же строки в цветах острова (Astra v3) —
    /// группы на тёмной подложке без рамок, светлый текст. Только в потоке интерфейса.
    public static bool IslandStyle;

    static Windows.UI.Color Ink => IslandStyle ? Island.IslandUi.Ink : Theme.Text;
    static Windows.UI.Color Sub => IslandStyle ? Island.IslandUi.Sub : Theme.TextSecondary;

    public static TextBlock Text(string text, double size = 14, bool secondary = false, bool bold = false) => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Theme.Brush(secondary ? Sub : Ink),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// Заголовок страницы.
    public static TextBlock Title(string text)
    {
        var t = Text(text, IslandStyle ? 20 : 28, bold: true);
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
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Theme.Brush(IslandStyle ? Island.IslandUi.RowOn : Theme.Separator), Margin = new Thickness(-16, 0, -16, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Background = Theme.Brush(IslandStyle ? Island.IslandUi.Group : Theme.Card),
            BorderBrush = Theme.Brush(IslandStyle ? Microsoft.UI.Colors.Transparent : Theme.Stroke),
            BorderThickness = new Thickness(IslandStyle ? 0 : 1),
            CornerRadius = new CornerRadius(IslandStyle ? 16 : 6),
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
    /// Кружки рисуем сами (не RadioButton): шаблон RadioButton, снятый с экрана посреди своей
    /// анимации, ронял XAML «Invalid attribute value Unknown for property Stroke» (WSH-E26).
    public static StackPanel Choice(IEnumerable<(string Title, bool Selected, Action Pick)> options)
    {
        var panel = new StackPanel { Padding = new Thickness(0, 6, 0, 6) };
        var dots = new List<Microsoft.UI.Xaml.Shapes.Ellipse>();
        var rings = new List<Microsoft.UI.Xaml.Shapes.Ellipse>();
        var list = options.ToList();
        var current = list.FindIndex(o => o.Selected);
        void Paint()
        {
            for (var i = 0; i < dots.Count; i++)
            {
                dots[i].Visibility = i == current ? Visibility.Visible : Visibility.Collapsed;
                rings[i].Stroke = Theme.Brush(i == current ? Theme.Accent : Sub);
                rings[i].Fill = Theme.Brush(i == current ? Theme.Accent : Microsoft.UI.Colors.Transparent);
            }
        }
        for (var i = 0; i < list.Count; i++)
        {
            var index = i;
            var ring = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 20, Height = 20, StrokeThickness = 1.5 };
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.Brush(Microsoft.UI.Colors.Black) };
            rings.Add(ring);
            dots.Add(dot);
            var mark = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, Children = { ring, dot } };
            var label = Text(list[i].Title);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { mark, label } };
            var b = new Button
            {
                Content = line,
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 40,
                Padding = new Thickness(4, 4, 4, 4),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, list[i].Title);
            b.Click += (_, _) =>
            {
                if (current == index) return;
                current = index;
                Paint();
                list[index].Pick();
            };
            panel.Children.Add(b);
        }
        Paint();
        return panel;
    }

    public static Button Button(string text, Action click)
    {
        var b = new Button { Content = Text(text, 13), MinWidth = 120 };
        b.Click += (_, _) => click();
        return b;
    }
}
