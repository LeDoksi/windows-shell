using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Kanban;
using WindowsShell.Ui;
using static WindowsShell.Island.IslandUi;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Island.Widgets;

/// Быстрая задача в острове (эскиз KanbanIsland, П12): проект, тип «Задача / Баг / Долг»,
/// эпик (последний пункт — «+ Новый эпик», поле раскрывается тут же), название, «Создать».
/// Эпик и задача создаются одним действием; не вышло — черновик остаётся, ошибка видна.
/// Та же форма — в окне канбана по «+ Задача»: меню показывает тот, кто её держит.
public sealed class KanbanQuick
{
    readonly KanbanService _k;
    readonly DispatcherQueue _queue;
    /// Показать меню под элементом: (элемент, "project" | "epic", пункты).
    readonly Action<FrameworkElement, string, List<MenuItem>> _menu;
    readonly Action _resized;
    readonly TextBox _title, _epicTitle;
    string? _project, _epic;
    string _type = "task";
    bool _newEpic, _busy;
    (string Text, bool Error)? _note;

    public StackPanel View { get; } = new() { Spacing = 8 };

    KanbanService K => _k;

    /// Задача создана (окно может закрыть форму).
    public event Action<KItem>? Created;

    public KanbanQuick(KanbanService k, DispatcherQueue queue, Action<FrameworkElement, string, List<MenuItem>> menu, Action resized)
    {
        _k = k;
        _queue = queue;
        _menu = menu;
        _resized = resized;
        _title = Input("Название задачи");
        _title.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Submit(); } };
        _epicTitle = Input("Название нового эпика");
        _epicTitle.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Submit(); } };
        var st = K.Settings;
        _project = K.Project(st.QuickProject)?.Id ?? K.Project(st.Project)?.Id ?? K.Projects.FirstOrDefault()?.Id;
        _epic = _project is null ? null : ValidEpic(_project, st.Epic(_project));
    }

    static TextBox Input(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        FontFamily = Theme.Font,
        FontSize = 14,
        MaxLength = 300,
        Background = B(Group),
        Foreground = B(Ink),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10, 8, 10, 8),
    };

    string? ValidEpic(string project, string? epic) => K.OpenEpics(project).Any(e => e.Id == epic) ? epic : null;

    public void Focus() => _title.Focus(FocusState.Programmatic);

    /// Проект задаёт хозяин формы (окно — открытый проект).
    public void SetProject(string project)
    {
        if (project == _project || K.Project(project) is null) return;
        _project = project;
        _epic = ValidEpic(project, K.Settings.Epic(project));
        _newEpic = false;
    }

    /// Эпик задаёт хозяин формы (окно — фильтр по эпику).
    public void SetEpicChoice(string? epic)
    {
        if (_project is null || _newEpic) return;
        _epic = ValidEpic(_project, epic);
    }

    /// Пересобрать (данные канбана поменялись). Введённый текст не теряется: поля те же.
    public void Build()
    {
        // Пересборка во время набора (пришли свежие данные) не должна отнимать фокус и курсор.
        var typing = _title.FocusState != FocusState.Unfocused ? _title : _epicTitle.FocusState != FocusState.Unfocused ? _epicTitle : null;
        var (start, length) = typing is null ? (0, 0) : (typing.SelectionStart, typing.SelectionLength);
        try { Layout(); }
        finally
        {
            if (typing != null && typing.IsEnabled && View.Children.Contains(typing))
                _queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (typing.FocusState != FocusState.Unfocused) return;
                    typing.Focus(FocusState.Programmatic);
                    typing.Select(Math.Min(start, typing.Text.Length), Math.Min(length, Math.Max(0, typing.Text.Length - start)));
                });
        }
    }

    void Layout()
    {
        View.Children.Clear();
        if (K.Projects.Count == 0) return;
        if (K.Project(_project) is null)
        {
            var st = K.Settings;
            _project = K.Project(st.QuickProject)?.Id ?? K.Projects[0].Id;
            _epic = ValidEpic(_project, st.Epic(_project));
        }
        View.Children.Add(Text("Быстрая задача", 15, bold: true));

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var project = Picker(K.Project(_project)?.Name ?? "Проект", "Проект", ProjectMenu);
        row.Children.Add(project);
        var types = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Padding = new Thickness(3), Background = B(Group), CornerRadius = new CornerRadius(11) };
        AutomationProperties.SetName(types, "Тип задачи");
        foreach (var (id, name) in Board.TypeChoice)
        {
            var t = id;
            var on = _type == id;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (id == "bug") content.Children.Add(Text("●", 11, RedText));
            if (id == "chore") content.Children.Add(Text("◆", 11, Sub));
            content.Children.Add(Text(name, 13, on ? Ink : Sub));
            var b = new Button
            {
                Content = content,
                Padding = new Thickness(12, 6, 12, 7),
                CornerRadius = new CornerRadius(8),
                Background = B(on ? SelectedSlot : Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(b, on ? $"{name} — выбрано" : name);
            b.Click += (_, _) => { _type = t; Build(); };
            types.Children.Add(b);
        }
        Grid.SetColumn(types, 1);
        row.Children.Add(types);
        View.Children.Add(row);

        var epic = K.Epic(_epic);
        View.Children.Add(Picker(_newEpic ? "+ Новый эпик" : epic is null ? "Без эпика" : $"{epic.Short} · {epic.Title}", "Эпик", EpicMenu));
        if (_newEpic)
        {
            _epicTitle.IsEnabled = !_busy;
            View.Children.Add(_epicTitle);
            View.Children.Add(Text("Эпик создастся вместе с задачей по «Создать».", 12, Sub, wrap: true));
        }
        _title.IsEnabled = !_busy;
        View.Children.Add(_title);

        var bottom = new Grid { ColumnSpacing = 10 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (_note is { } n) bottom.Children.Add(Text(n.Text, 12, n.Error ? AmberText : Green, wrap: true));
        var create = Btn(_busy ? "Создаю…" : "Создать", primary: true, click: Submit);
        create.IsEnabled = !_busy;
        Grid.SetColumn(create, 1);
        bottom.Children.Add(create);
        View.Children.Add(bottom);
    }

    Button Picker(string text, string name, Action<FrameworkElement> open)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Text(text, 13));
        var arrow = Text("▾", 13, Faint);
        Grid.SetColumn(arrow, 1);
        g.Children.Add(arrow);
        var b = new Button
        {
            Content = g,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = B(Group),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
        };
        AutomationProperties.SetName(b, $"{name}: {text}");
        b.Click += (_, _) => open(b);
        return b;
    }

    void ShowMenu(FrameworkElement at, string key, List<MenuItem> items) => _menu(at, key, items);

    void ProjectMenu(FrameworkElement at)
    {
        var items = K.Projects.Select(p =>
        {
            var id = p.Id;
            return new MenuItem { Text = p.Name, Checked = id == _project, Invoke = () => { _project = id; _epic = ValidEpic(id, K.Settings.Epic(id)); _newEpic = false; Build(); } };
        }).ToList();
        ShowMenu(at, "project", items);
    }

    void EpicMenu(FrameworkElement at)
    {
        if (_project is null) return;
        var items = new List<MenuItem> { new() { Text = "Без эпика", Checked = !_newEpic && _epic is null, Invoke = () => SetEpic(null) } };
        var epics = K.OpenEpics(_project);
        if (epics.Count > 0) items.Add(MenuItem.Sep);
        foreach (var e in epics)
        {
            var id = e.Id;
            items.Add(new MenuItem { Text = $"{e.Short} · {e.Title}", Checked = !_newEpic && _epic == id, Invoke = () => SetEpic(id) });
        }
        items.Add(MenuItem.Sep);
        items.Add(new MenuItem
        {
            Text = "+ Новый эпик",
            Checked = _newEpic,
            Invoke = () =>
            {
                _newEpic = true;
                Build();
                _resized();
                _queue.TryEnqueue(() => _epicTitle.Focus(FocusState.Programmatic));
            },
        });
        ShowMenu(at, "epic", items);
    }

    void SetEpic(string? epic)
    {
        _epic = epic;
        _newEpic = false;
        Build();
        _resized();
    }

    async void Submit()
    {
        var title = _title.Text.Trim();
        if (_busy || _project is null) return;
        if (title.Length == 0) { _note = ("Напиши название задачи.", true); Build(); _resized(); Focus(); return; }
        var newEpic = _newEpic ? _epicTitle.Text.Trim() : null;
        if (_newEpic && string.IsNullOrEmpty(newEpic)) { _note = ("Напиши название нового эпика.", true); Build(); _resized(); _epicTitle.Focus(FocusState.Programmatic); return; }
        var project = _project;
        _busy = true;
        _note = null;
        Build();
        try
        {
            var item = await K.CreateWithEpic(project, title, _type, _newEpic ? null : _epic, newEpic);
            _title.Text = "";
            _epicTitle.Text = "";
            _newEpic = false;
            _epic = item.EpicId;
            _note = ($"✓ {item.Id} создана", false);
            K.RememberQuick(project, item.EpicId);
            Created?.Invoke(item);
        }
        catch (KanbanException e)
        {
            if (e.KeptEpic is { } kept) { _newEpic = false; _epic = kept; _epicTitle.Text = ""; }
            _note = (e.Message, true);
        }
        catch (Exception e)
        {
            Log.Write($"канбан: быстрая задача в острове: {e}");
            _note = ("Задача не создалась, подробности в журнале оболочки.", true);
        }
        finally
        {
            _busy = false;
            Build();
            _resized();
            Focus();
        }
    }
}
