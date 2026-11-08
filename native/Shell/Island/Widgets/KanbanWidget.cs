using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Kanban;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island.Widgets;

/// Канбан в острове (эскиз KanbanIsland, П10–П12): «Нужно от тебя · N» — карточки, которые
/// ждут Георгия, с галочкой «Готово» одним нажатием и «Отменить» 6 секунд; ниже — быстрая
/// задача. Не вошли — кнопка входа; ошибка — текст и «Повторить». Вся доска — в окне.
public sealed class KanbanWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    const int Shown = 5;
    static readonly TimeSpan UndoFor = TimeSpan.FromSeconds(6);
    static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);

    KanbanQuick? _quick;
    KanbanService.Undo? _undo;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _undoTimer;

    public override string Id => "kanban";
    public override double Width => 600;

    KanbanService K => S.Kanban;

    protected override void OnOpen()
    {
        _quick ??= new KanbanQuick(S, Body, Resized);
        K.Changed += Later;
        // Данные старше полуминуты — перечитать (на сайте могли поменять).
        if (K.LoadedAt is not { } t || DateTime.Now - t > Fresh) _ = K.Refresh();
    }

    protected override void OnClose()
    {
        K.Changed -= Later;
        _undoTimer?.Stop();
        _undo = null;
    }

    public override void FocusFirst()
    {
        if (_quick != null && K.Api.SignedIn && K.Projects.Count > 0) _quick.Focus();
        else base.FocusFirst();
    }

    protected override void Build()
    {
        var waiting = Waiting();
        var head = new Grid { Margin = new Thickness(4, 0, 0, 0), ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(K.Api.SignedIn && K.LoadedAt != null ? $"Нужно от тебя · {waiting.Count}" : "Канбан", 18, bold: true));
        var board = Link("Открыть доску ↗", () => { Body.Sleep(restoreFocus: false); K.OpenWindow(); });
        Grid.SetColumn(board, 1);
        head.Children.Add(board);
        Root.Children.Add(head);

        if (!K.Api.SignedIn) { Root.Children.Add(SignIn()); return; }
        if (K.LoadedAt is null)
        {
            Root.Children.Add(K.Error is { } e ? Problem(e) : Note(K.Loading ? "Загружаю задачи…" : "Задачи ещё не загружены."));
            return;
        }
        if (K.Error is { } error) Root.Children.Add(Problem(error));

        if (_undo is { } u) Root.Children.Add(UndoRow(u));

        if (waiting.Count == 0) Root.Children.Add(Note("От тебя сейчас ничего не нужно."));
        else
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var item in waiting.Take(Shown)) list.Children.Add(Card(item));
            if (waiting.Count > Shown)
            {
                var more = Link($"Ещё {waiting.Count - Shown} — на доске →", () => { Body.Sleep(restoreFocus: false); K.OpenWindow(); });
                more.HorizontalAlignment = HorizontalAlignment.Left;
                list.Children.Add(more);
            }
            Root.Children.Add(list);
        }

        if (_quick != null && K.Projects.Count > 0)
        {
            Root.Children.Add(new Border { Height = 1, Background = B(Outline), Margin = new Thickness(4, 2, 4, 2) });
            _quick.Build();
            Root.Children.Add(_quick.View);
        }
    }

    /// «Нужно от тебя» во всех проектах: по проекту, потом по месту в колонке.
    List<KItem> Waiting()
    {
        var order = K.Projects.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return K.Items.Where(i => !i.Archived && i.Status == KStatus.Waiting && order.ContainsKey(i.ProjectId))
            .OrderBy(i => order[i.ProjectId]).ThenBy(i => i.Position)
            .ToList();
    }

    Border Card(KItem item)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        meta.Children.Add(Text(item.Id, 12, Faint));
        if (K.Epic(item.EpicId) is { } epic) meta.Children.Add(EpicChip(epic));
        if (item.Type == "bug") meta.Children.Add(Text("● баг", 12, RedText));
        else if (item.Type == "chore") meta.Children.Add(Text("◆ долг", 12, Sub));
        text.Children.Add(meta);
        var title = Text(item.Title, 14, wrap: true);
        title.MaxLines = 2;
        text.Children.Add(title);
        g.Children.Add(text);

        var open = Btn("Открыть", click: () => { Body.Sleep(restoreFocus: false); K.OpenWindow(item.ProjectId, item.Id); });
        Grid.SetColumn(open, 1);
        g.Children.Add(open);

        var check = Round("", 36, $"Готово: {item.Id}", out _, Chip);
        check.Click += (_, _) => Done(item);
        Grid.SetColumn(check, 2);
        g.Children.Add(check);

        var card = new Border
        {
            Background = B(Group),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 10, 10),
            Child = g,
        };
        AutomationProperties.SetName(card, $"{item.Id} {item.Title}");
        return card;
    }

    /// Метка эпика: цветная точка и номер (цвет тот же, что в окне).
    static FrameworkElement EpicChip(KEpic epic)
    {
        var c = Board.EpicColor(epic.Id);
        var color = Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        row.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = B(color), VerticalAlignment = VerticalAlignment.Center });
        var name = Text($"{epic.Short} · {epic.Title}", 12, Sub);
        name.MaxWidth = 220;
        row.Children.Add(name);
        return row;
    }

    void Done(KItem item)
    {
        _undo = K.Done(item);
        _undoTimer ??= MakeUndoTimer();
        _undoTimer.Stop();
        _undoTimer.Start();
        Rebuild();
    }

    Microsoft.UI.Dispatching.DispatcherQueueTimer MakeUndoTimer()
    {
        var t = S.Queue.CreateTimer();
        t.Interval = UndoFor;
        t.IsRepeating = false;
        t.Tick += (_, _) => { _undo = null; Rebuild(); };
        return t;
    }

    Border UndoRow(KanbanService.Undo u)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Text($"✓ {u.Item.Id} → Готово", 13, Green));
        var undo = Btn("Отменить", click: () =>
        {
            _undoTimer?.Stop();
            _undo = null;
            _ = K.UndoMove(u);
            Rebuild();
        });
        Grid.SetColumn(undo, 1);
        g.Children.Add(undo);
        return new Border { Background = B(Group), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 8, 6), Child = g };
    }

    static Border Note(string text) => new()
    {
        Background = B(Group),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(14, 14, 14, 14),
        Child = Text(text, 14, Sub, wrap: true),
    };

    Border Problem(string text)
    {
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Text(text, 13, AmberText, wrap: true));
        var again = Btn(K.Loading ? "Обновляю…" : "Повторить", click: () => _ = K.Refresh());
        again.IsEnabled = !K.Loading;
        Grid.SetColumn(again, 1);
        g.Children.Add(again);
        return new Border { Background = B(Group), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10, 10, 10), Child = g };
    }

    /// Не вошли: вход через Google в браузере (тот же, что в окне и на сайте).
    StackPanel SignIn()
    {
        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(Note("Канбан ещё не подключён. Войди тем же аккаунтом Google, что и на сайте — задачи появятся здесь."));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (K.WaitingForGoogle)
        {
            row.Children.Add(Text("Жду вход в браузере…", 13, Sub));
            row.Children.Add(Btn("Отмена", click: K.CancelGoogle));
        }
        else row.Children.Add(Btn("Войти через Google", primary: true, click: K.SignInGoogle));
        p.Children.Add(row);
        if (K.SignInError is { } e) p.Children.Add(Text(e, 12, AmberText, wrap: true));
        return p;
    }
}
