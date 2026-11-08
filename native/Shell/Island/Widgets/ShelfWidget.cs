using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static WindowsShell.Island.IslandUi;

namespace WindowsShell.Island.Widgets;

/// Полка файлов (эскиз Shelf, П17): ссылки на оригиналы. Клик — открыть, тянешь — файл
/// уходит в любое окно (StartDragAsync), ✕ — убрать с полки (сам файл не трогаем).
/// Пропавший файл — блёклая строка «Не найден» и «Убрать».
public sealed class ShelfWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    bool _confirm;

    public override string Id => "shelf";
    public override double Width => 602;

    protected override void OnOpen()
    {
        _confirm = false;
        S.Shelf.Changed += Later;
    }

    protected override void OnClose() => S.Shelf.Changed -= Later;

    protected override void Build()
    {
        var shelf = S.Shelf;
        var paths = shelf.Paths.ToList();
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(paths.Count == 0 ? "Полка" : $"Полка · {paths.Count}", 18, bold: true));
        if (paths.Count > 0)
        {
            var clear = _confirm
                ? Btn("Точно очистить?", primary: true, () => { shelf.Clear(); _confirm = false; })
                : Btn("Очистить полку", click: () => { _confirm = true; Rebuild(); });
            Grid.SetColumn(clear, 1);
            head.Children.Add(clear);
        }
        Root.Children.Add(head);

        if (paths.Count == 0)
        {
            Root.Children.Add(GroupBox(new Border
            {
                Padding = new Thickness(14),
                Child = Text("Перетащи файл к верхнему краю экрана и брось на «Полку» — здесь появится ссылка на него. Сам файл останется на месте.", 13, Sub, wrap: true),
            }));
            return;
        }

        var rows = new StackPanel { Spacing = 2 };
        foreach (var p in paths) rows.Children.Add(Row(p));
        Root.Children.Add(GroupBox(rows));
        Root.Children.Add(Text("Тяни файл с полки в любое окно. С полки убирается только ссылка — файл остаётся на месте. Полка живёт до очистки, в том числе после перезапуска.", 12, Faint, wrap: true));
    }

    UIElement Row(string path)
    {
        var exists = File.Exists(path) || Directory.Exists(path);
        var folder = Directory.Exists(path);
        var g = new Grid { ColumnSpacing = 12, Opacity = exists ? 1 : 0.55 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 32, Height = 32 };
        var fallback = Glyph(!exists ? "" : folder ? "" : "", 20, Sub);
        g.Children.Add(fallback);
        g.Children.Add(icon);
        if (exists)
            S.Icons.Get(path, (int)Math.Round(32 * Body.Screen.Scale), bmp =>
            {
                icon.Source = bmp;
                fallback.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
            });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = Path.GetFileName(path.TrimEnd('\\'));
        text.Children.Add(Text(string.IsNullOrEmpty(name) ? path : name, 14));
        text.Children.Add(exists
            ? Text(Path.GetFileName(Path.GetDirectoryName(path.TrimEnd('\\'))) ?? path, 12, Sub)
            : Text("Не найден — перемещён или удалён", 12, AmberText));
        ToolTipService.SetToolTip(text, path);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var remove = exists ? Btn("✕", click: () => S.Shelf.Remove(path)) : Btn("Убрать", click: () => S.Shelf.Remove(path));
        remove.PointerPressed += (_, e) => e.Handled = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Убрать с полки {name}");
        Grid.SetColumn(remove, 2);
        g.Children.Add(remove);
        var row = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(12), Background = B(Microsoft.UI.Colors.Transparent), Child = g };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, name);
        row.PointerEntered += (_, _) => row.Background = B(RowOn);
        row.PointerExited += (_, _) => row.Background = B(Microsoft.UI.Colors.Transparent);
        if (exists)
            Draggable(row, () => path, () => { Body.Sleep(restoreFocus: false); Apps.Launcher.Open(path); });
        return row;
    }
}
