using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Tail;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island.Widgets;

/// Корзина в острове (эскиз BinClips): «Корзина · N», открыть системную, очистить с
/// подтверждением прямо в теле; последние удалённые со «Восстановить». Список читаем в фоне:
/// в большой корзине файлов `$I…` тысячи.
public sealed class BinWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    const int Shown = 30;
    static readonly Color DangerBg = Color.FromArgb(255, 0x2A, 0x14, 0x14);
    static readonly Color DangerBtn = Color.FromArgb(255, 0x3A, 0x15, 0x15);

    List<BinItem>? _recent;
    bool _confirm;
    string? _error;
    int _version;

    public override string Id => "bin";
    public override double Width => 602;

    protected override void OnOpen()
    {
        _confirm = false;
        _error = null;
        S.Bin.Changed += Reload;
        S.Bin.Refresh();
        Reload();
    }

    protected override void OnClose() => S.Bin.Changed -= Reload;

    void Reload()
    {
        var v = ++_version;
        Task.Run(() =>
        {
            List<BinItem> list;
            try { list = S.Bin.Recent(Shown); }
            catch (Exception e) { Log.Error("остров: корзина", e); list = []; }
            S.Queue.TryEnqueue(() =>
            {
                if (v != _version || !IsOpen) return;
                _recent = list;
                if (S.Bin.Empty) _confirm = false;
                Rebuild();
            });
        });
    }

    protected override void Build()
    {
        var bin = S.Bin;
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(bin.Empty ? "Корзина" : $"Корзина · {bin.Count}", 18, bold: true));
        var open = Btn("Открыть корзину", click: () => { Body.Sleep(restoreFocus: false); RecycleBin.Open(); });
        Grid.SetColumn(open, 1);
        head.Children.Add(open);
        var clear = Btn("Очистить", click: () => { _confirm = true; Rebuild(); });
        clear.Background = B(DangerBtn);
        ((TextBlock)clear.Content).Foreground = B(RedText);
        clear.IsEnabled = !bin.Empty;
        Grid.SetColumn(clear, 2);
        head.Children.Add(clear);
        Root.Children.Add(head);
        if (!bin.Empty) Root.Children.Add(Text(bin.Summary, 12, Sub));

        if (_confirm && !bin.Empty)
        {
            var g = new Grid { ColumnSpacing = 8, Padding = new Thickness(12), Background = B(DangerBg), CornerRadius = new CornerRadius(14) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Text($"Удалить навсегда {RecycleBin.Objects(bin.Count)} ({RecycleBin.SizeText(bin.Size)})? Это не отменить.", 13, wrap: true));
            var cancel = Btn("Отмена", click: () => { _confirm = false; Rebuild(); });
            Grid.SetColumn(cancel, 1);
            g.Children.Add(cancel);
            var del = Btn("Удалить", click: () => { _confirm = false; bin.EmptyAll(); Log.Write("остров: корзина очищена"); Body.Sleep(); });
            del.Background = B(Red);
            ((TextBlock)del.Content).Foreground = B(Color.FromArgb(255, 0x1A, 0x06, 0x05));
            Grid.SetColumn(del, 2);
            g.Children.Add(del);
            Root.Children.Add(g);
            // Фокус на «Отмена»: Enter по ошибке ничего не удалит.
            cancel.Loaded += (_, _) => cancel.Focus(FocusState.Programmatic);
        }

        var rows = new StackPanel { Spacing = 2 };
        if (_recent == null) rows.Children.Add(new Border { Padding = new Thickness(12), Child = Text("…", 13, Sub) });
        else if (_recent.Count == 0) rows.Children.Add(new Border { Padding = new Thickness(12), Child = Text(bin.Empty ? "Корзина пуста. Сюда попадает то, что удаляешь." : "Список не прочитался — открой корзину Windows.", 13, Sub, wrap: true) });
        else foreach (var item in _recent) rows.Children.Add(Row(item));
        Root.Children.Add(GroupBox(rows));
        if (_error != null) Root.Children.Add(Text(_error, 12, RedText, wrap: true));
        Root.Children.Add(Text("Файлы можно бросить в корзину, перетащив их к верхнему краю экрана.", 12, Faint, wrap: true));
    }

    UIElement Row(BinItem item)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 24, Height = 24 };
        var fallback = Glyph(item.Folder ? "" : "", 18, Sub);
        g.Children.Add(fallback);
        g.Children.Add(icon);
        S.Icons.Get(item.DataPath, (int)Math.Round(24 * Body.Screen.Scale), bmp =>
        {
            icon.Source = bmp;
            fallback.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
        });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(item.Name, 14));
        text.Children.Add(Text($"{Path.GetFileName(Path.GetDirectoryName(item.OriginalPath)) ?? ""} · {BinFlyout.When(item.Deleted)}", 12, Sub));
        ToolTipService.SetToolTip(text, item.OriginalPath);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var restore = Btn("Восстановить", click: () =>
        {
            if (S.Bin.Restore(item)) { _recent?.Remove(item); _error = null; Log.Write($"остров: восстановлено {item.Name}"); }
            else _error = $"Не получилось восстановить «{item.Name}». Открой корзину и восстанови оттуда.";
            Rebuild();
        });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(restore, $"Восстановить {item.Name}");
        Grid.SetColumn(restore, 2);
        g.Children.Add(restore);
        return new Border { Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(12), Child = g };
    }
}
