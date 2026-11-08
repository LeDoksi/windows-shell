using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;
using WindowsShell.Config;
using WindowsShell.Island.Widgets;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using I = WindowsShell.Island.IslandUi;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Kanban;

/// Окно канбана в стиле острова (П13, эскиз KanbanWindow): шапка — проект, поиск, «Архив»,
/// «+ Задача»; под ней эпики с цветной точкой (клик — фильтр). Колонки как на сайте: Hold ·
/// Backlog · В работе · Нужно от тебя · Готово (последние пять, остальное — в архиве); внутри
/// колонки группы эпиков с полосой цвета. Круглая галочка закрывает задачу, внизу 6 секунд
/// «Отменить». Карточки перетаскиваются; клик открывает задачу справа. Помнит место и размер.
public sealed class KanbanWindow
{
    // Цвета карточки справа (CardPanel) — те же, что у острова.
    internal static readonly Color Muted = I.Sub;
    internal static readonly Color Faint = I.Faint;
    internal static readonly Color Violet = Color.FromArgb(255, 0xC7, 0x9B, 0xFF);
    internal static readonly Color Amber = I.Amber;
    internal static readonly Color Green = I.Green;
    internal static readonly Color Line = Color.FromArgb(0x17, 255, 255, 255);
    static readonly Color Accent = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    static readonly TimeSpan UndoFor = TimeSpan.FromSeconds(6);

    readonly KanbanService _k;
    readonly ShellConfig _config;
    readonly Window _window;
    readonly IntPtr _hwnd;
    readonly Grid _root;
    readonly OverlappedPresenter _presenter;
    readonly CardPanel _card;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _save, _undoTimer;

    // Постоянные части окна: их не пересобираем, чтобы не терялись фокус и введённый текст.
    readonly Grid _main = new();
    readonly Grid _top;
    readonly Button _projectPick, _archiveBtn, _addBtn;
    readonly TextBox _search;
    readonly KanbanQuick _quick;
    readonly Border _quickBox;
    readonly Border _legend = new();
    readonly Border _content = new();
    readonly Border _undoBar;
    readonly TextBlock _undoText = I.Text("", 13, I.Ink);
    KanbanService.Undo? _undo;

    string? _project;
    /// Клик по эпику в шапке: на доске только его задачи (ещё клик — все).
    string? _epicFilter;
    string _query = "";
    bool _archive, _quickOpen;
    string _signature = "";
    bool _closing, _dirty, _building;

    // Перетаскивание карточки.
    KItem? _drag;
    Panel? _ghostHost;
    readonly Border _ghost = new()
    {
        Height = 44,
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x4C, 0xC2, 0xFF)),
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0x4C, 0xC2, 0xFF)),
    };

    /// Окно закрыли (Alt+F4, «Закрыть» в меню или выключили в настройках).
    public event Action? Closed;

    public KanbanWindow(KanbanService kanban, ShellConfig config)
    {
        _k = kanban;
        _config = config;
        _window = new Window { Title = "Канбан" };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        Apps.OwnWindows.Register(_hwnd, "kanban", "Канбан", "");
        _project = kanban.Settings.Project;
        _card = new CardPanel(kanban);
        _card.Closed += () => { if (!_building) Build(force: true); };

        // ---- шапка ----
        _projectPick = new Button
        {
            Background = I.B(I.Group),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 7, 12, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _projectPick.Click += (_, _) => ProjectMenu();
        _search = new TextBox
        {
            PlaceholderText = "Поиск по названию и номеру",
            FontFamily = Theme.Font,
            FontSize = 13,
            Width = 260,
            MaxLength = 100,
            Background = I.B(I.Group),
            Foreground = I.B(I.Ink),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _search.TextChanged += (_, _) => { _query = _search.Text; Build(); };
        _search.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape && _search.Text.Length > 0) { e.Handled = true; _search.Text = ""; } };
        _archiveBtn = I.Btn("Архив", click: () => { _archive = !_archive; Build(force: true); });
        _addBtn = I.Btn("+ Задача", primary: true, click: () => ToggleQuick(!_quickOpen));
        var site = I.Link("Сайт ↗", kanban.OpenSite);
        var title = I.Text("Канбан", 18, bold: true);
        _top = new Grid { ColumnSpacing = 10, Padding = new Thickness(18, 14, 18, 10) };
        UIElement[] parts = [title, _projectPick, new Border(), _search, _archiveBtn, _addBtn, site];
        for (var c = 0; c < parts.Length; c++)
        {
            _top.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn((FrameworkElement)parts[c], c);
            _top.Children.Add(parts[c]);
        }

        _quick = new KanbanQuick(kanban, _window.DispatcherQueue, QuickMenu, () => { });
        _quick.Created += _ => _window.DispatcherQueue.TryEnqueue(() => Build(force: true));
        _quickBox = new Border
        {
            Margin = new Thickness(18, 0, 18, 10),
            Padding = new Thickness(14, 12, 14, 12),
            Background = I.B(I.Group),
            CornerRadius = new CornerRadius(16),
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _quick.View,
            Visibility = Visibility.Collapsed,
        };

        var undoRow = new Grid { ColumnSpacing = 12 };
        undoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        undoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        undoRow.Children.Add(_undoText);
        var undoBtn = I.Btn("Отменить", click: UndoLast);
        Grid.SetColumn(undoBtn, 1);
        undoRow.Children.Add(undoBtn);
        _undoBar = new Border
        {
            Child = undoRow,
            Background = I.B(I.RowOn),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _main.RowDefinitions.Add(new RowDefinition { Height = h });
        UIElement[] rows = [_top, _quickBox, _legend, _content, _undoBar];
        for (var r = 0; r < rows.Length; r++)
        {
            Grid.SetRow((FrameworkElement)rows[r], r);
            _main.Children.Add(rows[r]);
        }

        _root = new Grid { RequestedTheme = ElementTheme.Dark, Background = I.B(I.Body) };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.Children.Add(_main);
        Grid.SetColumn(_card.Root, 1);
        _root.Children.Add(_card.Root);
        _window.Content = _root;

        _root.KeyDown += OnKey;
        _root.RightTapped += (_, e) =>
        {
            if (e.Handled) return;
            GetCursorPos(out var p);
            _k.ShowMenu(p.X, p.Y, WindowMenu());
            e.Handled = true;
        };

        _presenter = OverlappedPresenter.Create();
        _window.AppWindow.SetPresenter(_presenter);
        SetDark(_hwnd);
        try { _window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "shell.ico")); } catch { }
        try { _window.AppWindow.TitleBar.BackgroundColor = I.Body; _window.AppWindow.TitleBar.ButtonBackgroundColor = I.Body; } catch { }

        _save = _window.DispatcherQueue.CreateTimer();
        _save.Interval = TimeSpan.FromSeconds(1);
        _save.IsRepeating = false;
        _save.Tick += (_, _) => SaveRect();
        _undoTimer = _window.DispatcherQueue.CreateTimer();
        _undoTimer.Interval = UndoFor;
        _undoTimer.IsRepeating = false;
        _undoTimer.Tick += (_, _) => HideUndo();
        _window.AppWindow.Changed += (_, e) => { if (e.DidPositionChange || e.DidSizeChange) { _save.Stop(); _save.Start(); } };
        _window.Activated += (_, e) =>
        {
            // Вернулись к окну — сразу свежие задачи (их могли поменять на сайте).
            if (e.WindowActivationState != WindowActivationState.Deactivated && _k.LoadedAt is { } t && DateTime.Now - t > TimeSpan.FromSeconds(5)) _ = _k.Refresh();
        };
        _window.Closed += (_, _) =>
        {
            _k.Changed -= OnChanged;
            _undoTimer.Stop();
            if (_save.IsRunning) SaveRect();
            Closed?.Invoke();
            // Закрыли не из настроек (Alt+F4) — выключаем и там.
            if (!_closing) _config.Update(_ => _k.Settings.Window = false);
        };

        Place();
        Apply(kanban.Settings);
        _k.Changed += OnChanged;
        Build(force: true);
        // При запуске оболочки окно появляется, но фокус не забирает; «Открыть» выводит вперёд.
        _window.AppWindow.Show(false);
        Log.Write("окно канбана открыто");
    }

    public void Apply(KanbanSettings s)
    {
        _presenter.IsAlwaysOnTop = s.WindowTopmost;
        Build();
    }

    /// Вывести вперёд; `item` — сразу открыть эту карточку (из острова).
    public void Activate(string? project, string? item = null)
    {
        if (project != null && project != _project) SwitchProject(project);
        if (item != null && _k.Item(item) is { } it)
        {
            // Карточка может быть в архиве — тогда показываем архив, чтобы её было видно и там.
            if (it.Archived) _archive = true;
            _card.Show(it);
            Build(force: true);
        }
        _window.Activate();
        ShellWindow.ForceForeground(_hwnd);
    }

    public void Close()
    {
        _closing = true;
        _window.Close();
    }

    void OnChanged()
    {
        // Во время перетаскивания доску не перестраиваем: карточка под мышью пропала бы.
        if (_drag != null) { _dirty = true; return; }
        Build();
        _card.Sync();
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.F5) { e.Handled = true; _ = _k.Refresh(); }
        else if (ctrl && e.Key == VirtualKey.N) { e.Handled = true; ToggleQuick(true); }
        else if (ctrl && e.Key == VirtualKey.F) { e.Handled = true; _search.Focus(FocusState.Programmatic); _search.SelectAll(); }
        else if (e.Key == VirtualKey.Escape && _card.Open) { e.Handled = true; _card.Hide(); }
        else if (e.Key == VirtualKey.Escape && _quickOpen) { e.Handled = true; ToggleQuick(false); }
    }

    List<MenuItem> WindowMenu() =>
    [
        new() { Text = "Обновить", Glyph = "", Invoke = () => _ = _k.Refresh() },
        new() { Text = "Сайт канбана", Glyph = "", Invoke = _k.OpenSite },
        MenuItem.Sep,
        new() { Text = "Поверх всех окон", Checked = _k.Settings.WindowTopmost, Invoke = () => _config.Update(_ => _k.Settings.WindowTopmost = !_k.Settings.WindowTopmost) },
        new() { Text = "Настройки…", Glyph = "", Invoke = _k.OpenSettings },
        MenuItem.Sep,
        new() { Text = "Закрыть", Glyph = "", Invoke = () => _config.Update(_ => _k.Settings.Window = false) },
    ];

    /// Сохранённое место, если оно на каком-то мониторе; иначе по центру монитора с курсором.
    void Place()
    {
        if (_k.Settings.WindowRect is { } r && r.W > 200 && r.H > 200)
        {
            var center = new POINT { X = r.X + r.W / 2, Y = r.Y + 20 };
            if (MonitorFromPoint(center, 0 /* MONITOR_DEFAULTTONULL */) != IntPtr.Zero)
            {
                _window.AppWindow.MoveAndResize(new RectInt32(r.X, r.Y, r.W, r.H));
                return;
            }
        }
        GetCursorPos(out var p);
        var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var work = MonitorInfo(mon).rcWork;
        var scale = MonitorScale(mon);
        var w = Math.Min((int)(1280 * scale), work.Right - work.Left - 40);
        var h = Math.Min((int)(760 * scale), work.Bottom - work.Top - 40);
        _window.AppWindow.MoveAndResize(new RectInt32(work.Left + (work.Right - work.Left - w) / 2, work.Top + (work.Bottom - work.Top - h) / 2, w, h));
    }

    void SaveRect()
    {
        _save.Stop();
        if (!GetWindowRect(_hwnd, out var r) || r.Width < 200 || r.Height < 200) return;
        var rect = (r.Left, r.Top, r.Width, r.Height);
        if (_k.Settings.WindowRect == rect) return;
        _config.Update(_ => _k.Settings.WindowRect = rect);
    }

    // ---------- шапка ----------

    void SwitchProject(string id)
    {
        _project = id;
        _epicFilter = null;
        _card.Hide();
        _quick.SetProject(id);
        Build(force: true);
    }

    void ProjectMenu()
    {
        var items = _k.Projects.Select(p =>
        {
            var id = p.Id;
            var (done, total) = _k.Progress(id);
            return new MenuItem
            {
                Text = $"{p.Name} · {done}/{total}",
                Checked = id == _project,
                Invoke = () => { if (id == _project) return; _k.RememberProject(id); SwitchProject(id); },
            };
        }).ToList();
        ShowMenuUnder(_projectPick, items);
    }

    /// Меню под элементом окна (физические пиксели экрана).
    void ShowMenuUnder(FrameworkElement at, List<MenuItem> items)
    {
        var scale = at.XamlRoot?.RasterizationScale ?? 1;
        var p = at.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, at.ActualHeight + 4));
        var pt = new POINT { X = (int)(p.X * scale), Y = (int)(p.Y * scale) };
        ClientToScreen(_hwnd, ref pt);
        _k.ShowMenu(pt.X, pt.Y, items);
    }

    void QuickMenu(FrameworkElement at, string key, List<MenuItem> items) => ShowMenuUnder(at, items);

    void ToggleQuick(bool open)
    {
        _quickOpen = open;
        if (open && _project != null)
        {
            _quick.SetProject(_project);
            _quick.SetEpicChoice(_epicFilter);
            _quick.Build();
        }
        _quickBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, _quick.Focus);
        PaintTop();
    }

    void PaintTop()
    {
        _projectPick.Content = I.Text($"{_k.Project(_project)?.Name ?? "Проект"}  ▾", 14, bold: true);
        AutomationProperties.SetName(_projectPick, $"Проект: {_k.Project(_project)?.Name}");
        SetButton(_archiveBtn, _archive ? "← Доска" : "Архив", _archive);
        SetButton(_addBtn, _quickOpen ? "Скрыть" : "+ Задача", null);
    }

    static void SetButton(Button b, string text, bool? on)
    {
        if (b.Content is TextBlock t) t.Text = text;
        if (on is { } o) b.Background = I.B(o ? I.SelectedSlot : I.Chip);
    }

    // ---------- доска ----------

    /// Перестроить окно; без `force` — только если что-то поменялось.
    void Build(bool force = false)
    {
        var s = _k.Settings;
        if (_k.Project(_project) is null) _project = _k.Projects.FirstOrDefault()?.Id;
        var sig = Signature(s);
        if (!force && sig == _signature) return;
        _signature = sig;
        _building = true;
        try { BuildAll(s); }
        catch (Exception e)
        {
            // Ошибка окна не должна ронять оболочку: пишем в журнал и показываем текстом.
            Log.Error("окно канбана", e);
            _signature = "";
            _content.Child = Center("Окно не перестроилось, подробности в журнале оболочки.");
        }
        finally { _building = false; }
    }

    void BuildAll(KanbanSettings s)
    {
        var signedIn = _k.Api.SignedIn;
        _top.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!signedIn && _quickOpen) ToggleQuick(false);
        PaintTop();
        if (_quickOpen) _quick.Build();
        _legend.Child = null;
        if (!signedIn)
        {
            _card.Hide();
            var view = SignInView.Build(_k, 340);
            _content.Child = new StackPanel
            {
                Spacing = 14, Width = 340, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { I.Text("Канбан", 20, bold: true), view },
            };
            return;
        }
        if (_project is null)
        {
            _content.Child = Center(_k.Error ?? (_k.LoadedAt is null ? "Загружаю канбан…" : "Проектов нет."));
            return;
        }
        var project = _k.Project(_project)!;
        _legend.Child = Legend(project);
        _content.Child = _archive ? ArchiveView(project) : Columns(project, s);
    }

    /// Всё, что видно в окне, одной строкой: не поменялось — не перестраиваем (нет мигания).
    string Signature(KanbanSettings s)
    {
        var sb = new StringBuilder();
        sb.Append(_project).Append('|').Append(_k.Api.SignedIn).Append('|').Append(_k.Error).Append('|').Append(_k.LoadedAt?.ToString("HH:mm"))
          .Append('|').Append(_k.WaitingForGoogle).Append('|').Append(_k.SignInError).Append('|').Append(_archive).Append('|').Append(_query.Trim()).Append('|').Append(_epicFilter)
          .Append('|').Append(string.Join(",", s.Shown)).Append(s.ShowDone).Append(s.Compact).Append('|').Append(_card.Current?.Id);
        foreach (var p in _k.Projects) sb.Append(p.Id).Append(p.Name).Append(';');
        foreach (var e in _k.Epics.Where(e => e.ProjectId == _project)) sb.Append(e.Id).Append(e.Title).Append(e.Status).Append(e.Position).Append(';');
        foreach (var i in _k.Items.Where(i => i.ProjectId == _project))
            sb.Append(i.Id).Append(i.Status).Append(i.Position).Append(i.Title).Append(i.EpicId).Append(i.Type).Append(i.Archived).Append(i.ClosedAt?.Ticks).Append(';');
        return sb.ToString();
    }

    /// Эпики под шапкой: цветная точка, название, «сделано/всего»; клик — только его задачи.
    FrameworkElement Legend(KProject project)
    {
        var grid = new Grid { Padding = new Thickness(14, 0, 18, 8), ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var list = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        // Эпики в работе: «active», и в них есть несделанное (или задач пока нет). Выбранный
        // остаётся, даже если его доделали, чтобы фильтр было видно и можно было снять.
        foreach (var e in _k.ActiveEpics(project.Id))
        {
            var (done, total) = _k.Progress(project.Id, e.Id);
            var on = e.Id == _epicFilter;
            if (!on && total > 0 && done >= total) continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(Dot(e.Id, 8));
            row.Children.Add(I.Text($"{e.Short} · {e.Title}", 13, on ? I.Ink : I.Sub));
            row.Children.Add(I.Text($"{done}/{total}", 12, I.Faint));
            var chip = new Button
            {
                Content = row,
                Padding = new Thickness(10, 5, 10, 6),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(0),
                Background = I.B(on ? I.SelectedSlot : Microsoft.UI.Colors.Transparent),
            };
            ToolTipService.SetToolTip(chip, on ? $"{e.Id} {e.Title}\nКлик — снова все задачи" : $"{e.Id} {e.Title}\nКлик — только задачи этого эпика");
            AutomationProperties.SetName(chip, on ? $"Эпик {e.Short} {e.Title} — фильтр включён" : $"Эпик {e.Short} {e.Title}");
            var id = e.Id;
            chip.Click += (_, _) =>
            {
                _epicFilter = _epicFilter == id ? null : id;
                // Новая задача идёт в выбранный эпик.
                if (_quickOpen) { _quick.SetEpicChoice(_epicFilter); _quick.Build(); }
                Build(force: true);
            };
            list.Children.Add(chip);
        }
        if (list.Children.Count == 0) list.Children.Add(new Border { Padding = new Thickness(6, 4, 6, 4), Child = I.Text("Эпиков в работе нет", 13, I.Faint) });
        grid.Children.Add(new ScrollViewer
        {
            Content = list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
        });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        var state = _k.Error ?? (_k.LoadedAt is { } t ? $"обновлено в {t:HH:mm}" : "загружаю…");
        var stateText = I.Text(state, 12, _k.Error != null ? I.AmberText : I.Faint);
        stateText.MaxWidth = 320;
        right.Children.Add(stateText);
        var week = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        week.Children.Add(I.Text("За неделю сделано", 12, I.Sub));
        week.Children.Add(I.Text(_k.DoneThisWeek(project.Id).ToString(), 15, I.Green, bold: true));
        right.Children.Add(week);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    FrameworkElement Columns(KProject project, KanbanSettings s)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(18, 4, 18, 14) };
        var shown = s.Shown.ToList();
        if (s.ShowDone) shown.Add(KStatus.Done);
        for (var c = 0; c < shown.Count; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 180 });
            var col = ColumnView(project, shown[c], s.Compact);
            Grid.SetColumn(col, c);
            grid.Children.Add(col);
        }
        if (shown.Count == 0) return Center("Все колонки выключены в настройках.");
        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
        };
    }

    /// Колонка: заголовок с числом, группы эпиков (в «Готово» — последние пять подряд).
    FrameworkElement ColumnView(KProject project, string status, bool compact)
    {
        var done = status == KStatus.Done;
        var all = Visible(project.Id, status);
        var items = done ? all.Take(Board.DoneShown).ToList() : all;
        var head = new Grid { Padding = new Thickness(6, 2, 6, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(I.Text(Board.StatusName(status), 14, status == KStatus.Waiting ? I.AmberText : I.Ink, bold: true));
        var count = I.Text(done ? $"{_k.Column(project.Id, status).Count}" : all.Count.ToString(), 13, I.Faint);
        Grid.SetColumn(count, 1);
        head.Children.Add(count);

        var list = new StackPanel { Spacing = 10, Padding = new Thickness(0, 0, 0, 24) };
        var cards = new List<FrameworkElement>();
        if (done)
        {
            var panel = new StackPanel { Spacing = 6 };
            foreach (var i in items) panel.Children.Add(Track(cards, CardView(i, compact)));
            list.Children.Add(panel);
            var more = Board.Archive(_k.Items, project.Id, "").Overflow.Count;
            if (more > 0)
            {
                var link = I.Link($"Ещё {more} — в архиве →", () => { _archive = true; Build(force: true); });
                link.HorizontalAlignment = HorizontalAlignment.Left;
                list.Children.Add(link);
            }
        }
        else
        {
            var epics = _k.Epics.Where(e => e.ProjectId == project.Id);
            foreach (var (epic, group) in Board.Groups(items, epics))
                list.Children.Add(GroupView(project.Id, epic, group, cards, compact));
        }
        if (items.Count == 0)
            list.Children.Add(new Border { Padding = new Thickness(8, 10, 8, 10), Child = I.Text(_query.Trim().Length > 0 ? "Ничего не нашлось" : done ? "Брось сюда карточку — задача готова" : "Пусто", 12, I.Faint) });

        var grid = new Grid { RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(head);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        var border = new Border
        {
            Background = I.B(I.Group),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(8, 10, 6, 8),
            Child = grid,
            AllowDrop = true,
        };
        DropTarget(border, list, cards, project.Id, status);
        return border;
    }

    static FrameworkElement Track(List<FrameworkElement> cards, FrameworkElement card)
    {
        cards.Add(card);
        return card;
    }

    /// Группа эпика: полоса его цвета слева, «E22 · НАЗВАНИЕ» и «3/7»; «Без эпика» — серая.
    FrameworkElement GroupView(string project, KEpic? epic, List<KItem> items, List<FrameworkElement> cards, bool compact)
    {
        var stack = new StackPanel { Spacing = 6 };
        var head = new Grid { Padding = new Thickness(2, 0, 2, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = I.Text(epic is null ? "БЕЗ ЭПИКА" : $"{epic.Short} · {epic.Title.ToUpperInvariant()}", 11, I.Sub, bold: true);
        name.CharacterSpacing = 30;
        head.Children.Add(name);
        if (epic != null)
        {
            var (done, total) = _k.Progress(project, epic.Id);
            var n = I.Text($"{done}/{total}", 11, I.Faint);
            Grid.SetColumn(n, 1);
            head.Children.Add(n);
            ToolTipService.SetToolTip(head, $"{epic.Id} {epic.Title}");
        }
        stack.Children.Add(head);
        foreach (var i in items) stack.Children.Add(Track(cards, CardView(i, compact)));
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = I.B(EpicColor(epic?.Id)) });
        Grid.SetColumn(stack, 1);
        g.Children.Add(stack);
        return g;
    }

    Border CardView(KItem item, bool compact)
    {
        var done = item.Status == KStatus.Done;
        var title = I.Text(item.Title, compact ? 12.5 : 13.5, done ? I.Sub : I.Ink, wrap: true);
        title.MaxLines = compact ? 2 : 4;
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        meta.Children.Add(I.Text(item.Id, 11, I.Faint));
        if (item.Type == "bug") meta.Children.Add(I.Text("● баг", 11, I.RedText));
        else if (item.Type == "chore") meta.Children.Add(I.Text("◆ долг", 11, I.Sub));
        if (item.CreatedBy == "claude") ToolTipService.SetToolTip(meta, "Создал Claude");
        // В «Готово» и в отфильтрованной доске эпик не виден по группе — метка на карточке.
        if (done && _k.Epic(item.EpicId) is { } epic)
        {
            var tag = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            tag.Children.Add(Dot(epic.Id, 6));
            tag.Children.Add(I.Text(epic.Short, 11, I.Faint));
            meta.Children.Add(tag);
        }

        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var check = Check(item);
        g.Children.Add(check);
        var text = new StackPanel { Spacing = compact ? 2 : 4, VerticalAlignment = VerticalAlignment.Center, Children = { meta, title } };
        Grid.SetColumn(text, 1);
        g.Children.Add(text);

        var selected = _card.Current?.Id == item.Id;
        var bg = I.B(I.RowOn);
        var card = new Border
        {
            Background = bg,
            BorderBrush = new SolidColorBrush(selected ? Accent : item.Status == KStatus.Waiting ? I.Amber : Microsoft.UI.Colors.Transparent),
            BorderThickness = selected ? new Thickness(1) : item.Status == KStatus.Waiting ? new Thickness(3, 0, 0, 0) : new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Padding = compact ? new Thickness(6, 5, 10, 5) : new Thickness(6, 8, 10, 8),
            Child = g,
            CanDrag = true,
            Tag = item,
        };
        AutomationProperties.SetName(card, $"{item.Id} {item.Title}");
        card.PointerEntered += (_, _) => card.Background = I.B(I.SelectedSlot);
        card.PointerExited += (_, _) => card.Background = bg;
        card.Tapped += (_, e) => { e.Handled = true; _card.Show(item); Build(force: true); };
        card.RightTapped += (_, e) =>
        {
            e.Handled = true;
            GetCursorPos(out var p);
            _k.ShowMenu(p.X, p.Y, CardMenu(item));
        };
        card.DragStarting += (_, e) =>
        {
            _drag = item;
            e.Data.SetText(item.Id);
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        card.DropCompleted += (_, _) =>
        {
            _drag = null;
            RemoveGhost();
            if (_dirty) { _dirty = false; Build(); _card.Sync(); }
        };
        return card;
    }

    /// Круглая галочка 30 px (зона 34): не готово — закрыть; готово (зелёная) — вернуть в работу.
    Button Check(KItem item)
    {
        var done = item.Status == KStatus.Done;
        var icon = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 13, Foreground = I.B(done ? I.Body : I.Faint), Opacity = done ? 1 : 0 };
        var b = new Button
        {
            Content = icon,
            Width = 30,
            Height = 30,
            Margin = new Thickness(2),
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(done ? 0 : 1.5),
            BorderBrush = I.B(I.Faint),
            Background = I.B(done ? I.Green : Microsoft.UI.Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(b, done ? $"Вернуть в работу: {item.Id}" : $"Готово: {item.Id}");
        ToolTipService.SetToolTip(b, done ? "Вернуть в работу" : "Готово");
        if (!done)
        {
            b.PointerEntered += (_, _) => icon.Opacity = 1;
            b.PointerExited += (_, _) => icon.Opacity = 0;
        }
        b.Tapped += (_, e) => e.Handled = true;
        b.Click += (_, _) =>
        {
            var undo = done ? _k.MoveWithUndo(item, KStatus.Doing) : _k.Done(item);
            ShowUndo(undo, done ? $"{item.Id} → В работе" : $"{item.Id} → Готово");
        };
        return b;
    }

    /// Карточки колонки с учётом фильтра по эпику и поиска.
    List<KItem> Visible(string project, string status) =>
        _k.Column(project, status).Where(i => (_epicFilter is null || i.EpicId == _epicFilter) && Board.Matches(i, _query)).ToList();

    List<MenuItem> CardMenu(KItem item)
    {
        var items = new List<MenuItem> { new() { Text = "Открыть", Glyph = "", Invoke = () => { _card.Show(item); Build(force: true); } } };
        items.Add(MenuItem.Sep);
        foreach (var status in KStatus.All.Where(x => x != item.Status))
        {
            var to = status;
            items.Add(new() { Text = $"В «{KStatus.Name(to)}»", Invoke = () => ShowUndo(_k.MoveWithUndo(item, to), $"{item.Id} → {KStatus.Name(to)}") });
        }
        items.Add(MenuItem.Sep);
        if (item.Archived) items.Add(new() { Text = "Вернуть из архива", Invoke = () => _ = _k.Restore(item) });
        else if (item.Status == KStatus.Done) items.Add(new() { Text = "В архив", Invoke = () => _ = _k.Archive(item) });
        items.Add(new() { Text = $"Копировать {item.Id}", Glyph = "", Invoke = () => Copy(item.Id) });
        items.Add(new() { Text = "Копировать номер и название", Glyph = "", Invoke = () => Copy($"{item.Id} {item.Title}") });
        return items;
    }

    static void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    // ---------- «Отменить» ----------

    void ShowUndo(KanbanService.Undo undo, string text)
    {
        _undo = undo;
        _undoText.Text = $"✓ {text}";
        _undoBar.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    void HideUndo()
    {
        _undoTimer.Stop();
        _undo = null;
        _undoBar.Visibility = Visibility.Collapsed;
    }

    void UndoLast()
    {
        if (_undo is { } u) _ = _k.UndoMove(u);
        HideUndo();
    }

    // ---------- архив ----------

    /// Архив проекта: «В архиве» (с «Вернуть») и «Готово», не поместившиеся в колонку; поиск
    /// сверху тот же. Клик по строке — карточка справа.
    FrameworkElement ArchiveView(KProject project)
    {
        var (archived, overflow) = Board.Archive(_k.Items, project.Id, _query);
        if (_epicFilter != null)
        {
            archived = archived.Where(i => i.EpicId == _epicFilter).ToList();
            overflow = overflow.Where(i => i.EpicId == _epicFilter).ToList();
        }
        var stack = new StackPanel { Spacing = 8, Padding = new Thickness(18, 4, 18, 24), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(Section($"В архиве · {archived.Count}"));
        if (archived.Count == 0) stack.Children.Add(Empty());
        foreach (var i in archived) stack.Children.Add(ArchiveRow(i, restore: true));
        stack.Children.Add(Section($"Готово, не поместилось в колонку · {overflow.Count}"));
        if (overflow.Count == 0) stack.Children.Add(Empty());
        foreach (var i in overflow) stack.Children.Add(ArchiveRow(i, restore: false));
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        FrameworkElement Section(string text)
        {
            var t = I.Text(text, 14, bold: true);
            t.Margin = new Thickness(4, 10, 0, 2);
            return t;
        }
        FrameworkElement Empty() => new Border { Padding = new Thickness(6, 4, 6, 4), Child = I.Text(_query.Trim().Length > 0 ? "Ничего не нашлось" : "Пусто", 12, I.Faint) };
    }

    FrameworkElement ArchiveRow(KItem item, bool restore)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(I.Text(item.Id, 12, I.Faint));
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (_k.Epic(item.EpicId) is { } epic) title.Children.Add(Dot(epic.Id, 7));
        if (Board.TypeName(item.Type) is { } type) title.Children.Add(I.Text(type, 12, item.Type == "bug" ? I.RedText : I.Sub));
        title.Children.Add(I.Text(item.Title, 13.5, restore ? I.Sub : I.Ink));
        Grid.SetColumn(title, 1);
        g.Children.Add(title);
        var when = item.ClosedAt ?? item.ArchivedAt;
        var meta = I.Text($"{(restore && item.Status != KStatus.Done ? Board.StatusName(item.Status) + " · " : "")}{(when is { } w ? w.ToLocalTime().ToString("dd.MM.yy") : "")}", 12, I.Faint);
        Grid.SetColumn(meta, 2);
        g.Children.Add(meta);
        if (restore)
        {
            var back = I.Btn("Вернуть", click: () => _ = _k.Restore(item));
            back.Tapped += (_, e) => e.Handled = true;
            AutomationProperties.SetName(back, $"Вернуть {item.Id} из архива");
            Grid.SetColumn(back, 3);
            g.Children.Add(back);
        }
        var selected = _card.Current?.Id == item.Id;
        var bg = I.B(I.Group);
        var row = new Border
        {
            Child = g,
            Background = bg,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 8, 8),
            MinHeight = 44,
            BorderThickness = new Thickness(selected ? 1 : 0),
            BorderBrush = new SolidColorBrush(Accent),
        };
        AutomationProperties.SetName(row, $"{item.Id} {item.Title}");
        row.PointerEntered += (_, _) => row.Background = I.B(I.RowOn);
        row.PointerExited += (_, _) => row.Background = bg;
        row.Tapped += (_, e) => { e.Handled = true; _card.Show(item); Build(force: true); };
        row.RightTapped += (_, e) =>
        {
            e.Handled = true;
            GetCursorPos(out var p);
            _k.ShowMenu(p.X, p.Y, CardMenu(item));
        };
        return row;
    }

    // ---------- перетаскивание ----------

    void DropTarget(Border border, StackPanel list, List<FrameworkElement> cards, string project, string status)
    {
        border.DragOver += (_, e) =>
        {
            if (_drag is null) return;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
            // В «Готово» место не выбирается: свежие сверху.
            if (status == KStatus.Done) { border.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC2, 0xFF)); return; }
            ShowGhost(list, cards, Before(cards, e.GetPosition(list).Y, list));
        };
        border.DragLeave += (_, _) =>
        {
            border.Background = I.B(I.Group);
            if (_ghostHost != null && _ghost.Parent != null && IsInside(_ghostHost, list)) RemoveGhost();
        };
        border.Drop += (_, e) =>
        {
            border.Background = I.B(I.Group);
            var before = Before(cards, e.GetPosition(list).Y, list);
            RemoveGhost();
            if (_drag is not { } item) return;
            if (status == KStatus.Done)
            {
                if (item.Status != KStatus.Done) ShowUndo(_k.Done(item), $"{item.Id} → Готово");
                return;
            }
            // Место по порядку колонки (группы эпиков — только вид): встаёт перед карточкой под мышью.
            var column = _k.Column(project, status).Where(i => i.Id != item.Id).ToList();
            var index = before is { Tag: KItem target } ? column.FindIndex(i => i.Id == target.Id) : column.Count;
            if (index < 0) index = column.Count;
            if (item.Status == status)
            {
                var was = _k.Column(project, status).FindIndex(i => i.Id == item.Id);
                if (was == index) return;
            }
            _ = _k.Move(item, status, KanbanService.PositionAt(column, index));
        };
    }

    /// Карточка, перед которой встанет перетаскиваемая (null — в конец).
    FrameworkElement? Before(List<FrameworkElement> cards, double y, StackPanel list)
    {
        foreach (var el in cards)
        {
            if (el is not { Tag: KItem item } || item.Id == _drag?.Id) continue;
            var top = el.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (y < top + el.ActualHeight / 2) return el;
        }
        return null;
    }

    static bool IsInside(DependencyObject child, DependencyObject root)
    {
        for (var p = child; p != null; p = VisualTreeHelper.GetParent(p))
            if (p == root) return true;
        return false;
    }

    void ShowGhost(StackPanel list, List<FrameworkElement> cards, FrameworkElement? before)
    {
        // Панель, куда встанет призрак: та же, где карточка-цель; в конец — последняя группа.
        var host = before?.Parent as Panel ?? cards.LastOrDefault(c => c.Tag is KItem i && i.Id != _drag?.Id)?.Parent as Panel ?? list;
        var at = before is null ? host.Children.Count : host.Children.IndexOf(before);
        if (at < 0) at = host.Children.Count;
        if (_ghostHost == host)
        {
            var cur = host.Children.IndexOf(_ghost);
            if (cur >= 0 && (cur == at || cur == at - 1)) return;
        }
        RemoveGhost();
        if (at > host.Children.Count) at = host.Children.Count;
        host.Children.Insert(at, _ghost);
        _ghostHost = host;
    }

    void RemoveGhost()
    {
        // Хозяина держим явно: у отцепленной панели WinUI не отдаёт Parent (урок PR #44).
        if (_ghostHost != null) { var i = _ghostHost.Children.IndexOf(_ghost); if (i >= 0) _ghostHost.Children.RemoveAt(i); }
        else if (_ghost.Parent is Panel p) p.Children.Remove(_ghost);
        _ghostHost = null;
    }

    // ---------- мелочи ----------

    static Color EpicColor(string? epicId)
    {
        var c = Board.EpicColor(epicId);
        return Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
    }

    static Border Dot(string epicId, double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = I.B(EpicColor(epicId)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    internal static TextBlock Text(double size, Color color, string text = "") => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        Foreground = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static FrameworkElement Center(string text)
    {
        var t = I.Text(text, 14, I.Sub, wrap: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        return t;
    }

    /// Полоса прогресса.
    internal static Grid Bar(double fraction, Color color)
    {
        var bar = new Grid { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(fraction, 0, 1), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - Math.Clamp(fraction, 0, 1), GridUnitType.Star) });
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(color) });
        return bar;
    }
}
