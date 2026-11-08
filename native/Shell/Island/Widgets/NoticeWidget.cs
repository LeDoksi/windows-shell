using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Apps;
using WindowsShell.Notices;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island.Widgets;

/// Центр уведомлений в острове (П18, вариант А): группы по приложениям, новые сверху; открыли —
/// всё прочитано; «Очистить» у группы и «Очистить всё»; клик по уведомлению открывает
/// приложение. Своё «Не беспокоить» — тихо копит. Подсказывает, если баннеры Windows ещё
/// показываются (тогда остров только собирает, чтобы не было дублей) и если нет доступа.
public sealed class NoticeWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    const int PerGroup = 4;
    static readonly Color HintBg = Color.FromArgb(255, 0x2A, 0x22, 0x12);

    readonly HashSet<string> _expanded = [];

    public override string Id => "notif";
    public override double Width => 560;

    protected override void OnOpen()
    {
        _expanded.Clear();
        S.Notifications.Changed += Later;
        S.Config.Changed += Later;
        // П18: открыли центр — всё прочитано.
        S.Notifications.MarkRead();
    }

    protected override void OnClose()
    {
        S.Notifications.Changed -= Later;
        S.Config.Changed -= Later;
        S.Notifications.MarkRead();
    }

    protected override void Build()
    {
        var n = S.Notifications;
        var quiet = S.Config.Island.Quiet;
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(n.Store.Items.Count == 0 ? "Уведомления" : $"Уведомления · {n.Store.Items.Count}", 18, bold: true));
        var dnd = Btn(quiet ? "  Не беспокоить ✓" : "Не беспокоить", click: () => S.Config.Update(c => c.Island.Quiet = !c.Island.Quiet));
        dnd.Opacity = quiet ? 1 : 0.75;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dnd, $"Не беспокоить: {(quiet ? "включено" : "выключено")}");
        ToolTipService.SetToolTip(dnd, "Уведомления тихо собираются здесь: без всплытий и звука. После выключения не высыпаются.");
        Grid.SetColumn(dnd, 1);
        head.Children.Add(dnd);
        var clear = Btn("Очистить всё", click: () => { n.ClearAll(); Log.Write("остров: уведомления очищены"); });
        clear.IsEnabled = n.Store.Items.Count > 0;
        Grid.SetColumn(clear, 2);
        head.Children.Add(clear);
        Root.Children.Add(head);

        if (!n.Listening)
            Root.Children.Add(Hint($"Windows не даёт оболочке читать уведомления (доступ: {n.Access}). Разреши доступ к уведомлениям для приложений в параметрах конфиденциальности.",
                "Открыть параметры", "ms-settings:privacy-notifications"));
        else if (!n.DoNotDisturb)
            Root.Children.Add(Hint("Баннеры Windows ещё включены, поэтому остров пока только собирает уведомления сюда — без всплытий, чтобы не было дублей. Включи в Windows «Не беспокоить» — тогда всплывать будет только остров.",
                "Параметры уведомлений ↗", "ms-settings:notifications"));

        var groups = n.Store.Groups();
        if (groups.Count == 0)
        {
            Root.Children.Add(new Border { Padding = new Thickness(12, 18, 12, 18), Child = Text(quiet ? "Тихо. Новые уведомления соберутся здесь." : "Новых уведомлений нет. Здесь хранится всё за 3 дня.", 13, Sub, wrap: true) });
            return;
        }
        foreach (var g in groups) Root.Children.Add(Group(g));
        Root.Children.Add(Text("Хранятся 3 дня. Оригиналы остаются и в центре Windows (Win+N).", 12, Faint, wrap: true));
    }

    UIElement Hint(string text, string button, string uri)
    {
        var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 10, 12, 10), Background = B(HintBg), CornerRadius = new CornerRadius(14) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Text(text, 12, AmberText, wrap: true));
        var b = Btn(button, click: () => { Body.Sleep(restoreFocus: false); Launcher.Open(uri); });
        b.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(b, 1);
        g.Children.Add(b);
        return g;
    }

    UIElement Group(NoticeGroup g)
    {
        var box = new StackPanel { Spacing = 2 };
        var head = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 6, 6, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(IslandHost.NoticeIcon(g.Items[0], 22));
        var name = Text(g.Items.Count > 1 ? $"{g.App} · {g.Items.Count}" : g.App, 13, Sub, bold: true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var clear = Link("Очистить", () => S.Notifications.Clear(g.App));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clear, $"Очистить уведомления {g.App}");
        Grid.SetColumn(clear, 2);
        head.Children.Add(clear);
        box.Children.Add(head);
        var open = _expanded.Contains(g.App);
        foreach (var item in open ? g.Items : g.Items.Take(PerGroup)) box.Children.Add(Item(item));
        if (!open && g.Items.Count > PerGroup)
            box.Children.Add(Link($"Ещё {g.Items.Count - PerGroup}", () => { _expanded.Add(g.App); Rebuild(); }));
        return GroupBox(box);
    }

    UIElement Item(Notice n)
    {
        var lines = new StackPanel { Spacing = 2 };
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Text(n.Title, 14, bold: true);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        top.Children.Add(title);
        var when = Text(When(n.Time), 12, Faint);
        Grid.SetColumn(when, 1);
        top.Children.Add(when);
        lines.Children.Add(top);
        if (n.Body.Length > 0)
        {
            var body = Text(n.Body, 13, Sub, wrap: true);
            body.MaxLines = 2;
            body.TextTrimming = TextTrimming.CharacterEllipsis;
            lines.Children.Add(body);
        }
        var row = Row(lines, name: $"{n.App}: {n.Title}. {When(n.Time)}. Открыть приложение");
        if (n.Body.Length > 80) ToolTipService.SetToolTip(row, n.Body.Length > 800 ? n.Body[..800] + "…" : n.Body);
        row.Click += (_, _) =>
        {
            Body.Sleep(restoreFocus: false);
            S.Island?.OpenNotice(n);
        };
        return row;
    }

    static string When(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return "сейчас";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин";
        if (t.LocalDateTime.Date == DateTime.Today) return t.LocalDateTime.ToString("HH:mm");
        if (t.LocalDateTime.Date == DateTime.Today.AddDays(-1)) return "вчера " + t.LocalDateTime.ToString("HH:mm");
        return t.LocalDateTime.ToString("dd.MM HH:mm");
    }
}
