using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;
using WindowsShell.Config;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Kanban;

/// Окно канбана (design-v2 п. 9б): слева все проекты с полосой прогресса, сверху поле быстрой
/// задачи и полосы эпиков, «сделано за неделю»; колонки В работе · Ждёт · Бэклог · Пауза,
/// «Готово» свёрнуто внизу. Карточки перетаскиваются между колонками и внутри колонки, клик
/// открывает задачу справа (правка на месте). Помнит монитор, место и размер.
public sealed class KanbanWindow
{
    internal static readonly Color Muted = Color.FromArgb(255, 0xA9, 0xAD, 0xB4);
    internal static readonly Color Faint = Color.FromArgb(255, 0x7C, 0x81, 0x8A);
    internal static readonly Color Violet = Color.FromArgb(255, 0xB3, 0x9C, 0xFF);
    internal static readonly Color Amber = Color.FromArgb(255, 0xFF, 0xC0, 0x4D);
    internal static readonly Color Green = Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
    internal static readonly Color Line = Color.FromArgb(0x17, 255, 255, 255);
    static readonly Color CardBg = Color.FromArgb(0xFF, 0x25, 0x28, 0x2D);
    static readonly Color CardHover = Color.FromArgb(0xFF, 0x2C, 0x2F, 0x35);
    static readonly Color CardLine = Color.FromArgb(0xFF, 0x33, 0x36, 0x3C);
    static readonly Color Column = Color.FromArgb(0x08, 255, 255, 255);

    readonly KanbanService _k;
    readonly ShellConfig _config;
    readonly Window _window;
    readonly IntPtr _hwnd;
    readonly Grid _root;
    readonly OverlappedPresenter _presenter;
    readonly StackPanel _projects = new() { Spacing = 2 };
    readonly TextBlock _state = Text(11, Faint);
    readonly Grid _main = new();
    readonly TextBox _quick;
    readonly CardPanel _card;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _save;
    string? _project;
    string? _quickEpic;
    string _signature = "";
    bool _closing, _doneOpen, _dirty, _building;

    // Перетаскивание карточки.
    KItem? _drag;
    readonly Border _ghost = new()
    {
        Height = 44,
        CornerRadius = new CornerRadius(7),
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
        Apps.OwnWindows.Register(_hwnd, "kanban", "Канбан", "\uE9D5");
        _project = kanban.Settings.Project;
        _quick = new TextBox { PlaceholderText = "Новая задача… Enter", FontFamily = Theme.Font, FontSize = 14, MaxLength = 300, VerticalAlignment = VerticalAlignment.Center };
        _quick.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; AddQuick(); } };
        _card = new CardPanel(kanban);
        _card.Closed += () => { if (!_building) Build(force: true); };

        var sideTitle = Text(12, Faint, "ПРОЕКТЫ");
        sideTitle.Margin = new Thickness(8, 0, 0, 6);
        sideTitle.CharacterSpacing = 40;
        var side = new Grid
        {
            Width = 220,
            Padding = new Thickness(10, 14, 10, 10),
            Background = new SolidColorBrush(Color.FromArgb(0x08, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Line),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        side.Children.Add(sideTitle);
        var scroll = new ScrollViewer { Content = _projects, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        side.Children.Add(scroll);
        _state.Margin = new Thickness(8, 6, 8, 0);
        _state.TextWrapping = TextWrapping.Wrap;
        _state.TextTrimming = TextTrimming.None;
        Grid.SetRow(_state, 2);
        side.Children.Add(_state);

        _root = new Grid { RequestedTheme = ElementTheme.Dark };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.Children.Add(side);
        Grid.SetColumn(_main, 1);
        _root.Children.Add(_main);
        Grid.SetColumn(_card.Root, 2);
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

        ApplySurface();
        Glass.Changed += ApplySurface;
        _presenter = OverlappedPresenter.Create();
        _window.AppWindow.SetPresenter(_presenter);
        SetDark(_hwnd);
        try { _window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "shell.ico")); } catch { }

        _save = _window.DispatcherQueue.CreateTimer();
        _save.Interval = TimeSpan.FromSeconds(1);
        _save.IsRepeating = false;
        _save.Tick += (_, _) => SaveRect();
        _window.AppWindow.Changed += (_, e) => { if (e.DidPositionChange || e.DidSizeChange) { _save.Stop(); _save.Start(); } };
        _window.Activated += (_, e) =>
        {
            // Вернулись к окну — сразу свежие задачи (их могли поменять на сайте).
            if (e.WindowActivationState != WindowActivationState.Deactivated && _k.LoadedAt is { } t && DateTime.Now - t > TimeSpan.FromSeconds(5)) _ = _k.Refresh();
        };
        _window.Closed += (_, _) =>
        {
            Glass.Changed -= ApplySurface;
            _k.Changed -= OnChanged;
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
        ApplySurface();
        Build();
    }

    public void Activate(string? project)
    {
        if (project != null && project != _project) { _project = project; _card.Hide(); Build(force: true); }
        _window.Activate();
        ShellWindow.ForceForeground(_hwnd);
        _quick.Focus(FocusState.Programmatic);
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
        else if (ctrl && e.Key == VirtualKey.N) { e.Handled = true; _quick.Focus(FocusState.Programmatic); }
        else if (e.Key == VirtualKey.Escape && _card.Open) { e.Handled = true; _card.Hide(); }
    }

    List<MenuItem> WindowMenu() =>
    [
        new() { Text = "Обновить", Glyph = "", Invoke = () => _ = _k.Refresh() },
        new() { Text = "Сайт канбана", Glyph = "", Invoke = _k.OpenSite },
        MenuItem.Sep,
        new() { Text = "Поверх всех окон", Checked = _k.Settings.WindowTopmost, Invoke = () => _config.Update(_ => _k.Settings.WindowTopmost = !_k.Settings.WindowTopmost) },
        new() { Text = "Настройки…", Glyph = "", Invoke = _k.OpenSettings },
        MenuItem.Sep,
        new() { Text = "Закрыть", Glyph = "", Invoke = () => _config.Update(_ => _k.Settings.Window = false) },
    ];

    void ApplySurface()
    {
        var tint = _k.Settings.Surface switch
        {
            KanbanSettings.SurfaceGlass => false,
            KanbanSettings.SurfaceTint => true,
            _ => Glass.Tint,
        };
        if (tint)
        {
            _window.SystemBackdrop = null;
            _root.Background = new SolidColorBrush(Color.FromArgb(255, 0x1B, 0x1D, 0x21));
        }
        else
        {
            if (_window.SystemBackdrop is not AlwaysActiveAcrylic) _window.SystemBackdrop = new AlwaysActiveAcrylic();
            _root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

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
        var w = Math.Min((int)(1100 * scale), work.Right - work.Left - 40);
        var h = Math.Min((int)(700 * scale), work.Bottom - work.Top - 40);
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

    // ---------- доска ----------

    /// Перестроить окно; без `force` — только если что-то поменялось.
    void Build(bool force = false)
    {
        var s = _k.Settings;
        if (_k.Project(_project) is null) _project = _k.Projects.FirstOrDefault()?.Id;
        var sig = Signature(s);
        if (!force && sig == _signature) return;
        _signature = sig;
        // Поле быстрой задачи переезжает в новую шапку: печать не должна прерываться.
        var typing = _quick.FocusState != FocusState.Unfocused;
        var (start, length) = (_quick.SelectionStart, _quick.SelectionLength);
        _building = true;
        try { BuildAll(s); }
        finally
        {
            _building = false;
            if (typing && _quick.Parent != null)
                _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    _quick.Focus(FocusState.Programmatic);
                    _quick.Select(Math.Min(start, _quick.Text.Length), Math.Min(length, Math.Max(0, _quick.Text.Length - start)));
                });
        }
    }

    void BuildAll(KanbanSettings s)
    {
        _state.Text = _k.Error ?? (_k.LoadedAt is { } t ? $"Обновлено в {t:HH:mm}" : _k.Api.SignedIn ? "Загружаю…" : "");
        _state.Foreground = new SolidColorBrush(_k.Error != null ? Theme.Warning : Faint);
        BuildProjects();
        _main.Children.Clear();
        _main.RowDefinitions.Clear();
        if (!_k.Api.SignedIn)
        {
            _card.Hide();
            var view = SignInView.Build(_k, 340);
            var title = Text(20, Microsoft.UI.Colors.White, "Канбан");
            title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            _main.Children.Add(new StackPanel
            {
                Spacing = 14, Width = 340, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { title, view },
            });
            return;
        }
        if (_project is null)
        {
            _main.Children.Add(Center(_k.Error ?? (_k.LoadedAt is null ? "Загружаю канбан…" : "Проектов нет.")));
            return;
        }
        var project = _k.Project(_project)!;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _main.RowDefinitions.Add(new RowDefinition { Height = h });
        _main.Children.Add(Top(project));
        var epics = Epics(project);
        Grid.SetRow(epics, 1);
        _main.Children.Add(epics);
        var columns = Columns(project, s);
        Grid.SetRow(columns, 2);
        _main.Children.Add(columns);
        if (s.ShowDone)
        {
            var done = Done(project, s);
            Grid.SetRow(done, 3);
            _main.Children.Add(done);
        }
    }

    /// Всё, что видно в окне, одной строкой: не поменялось — не перестраиваем (нет мигания).
    string Signature(KanbanSettings s)
    {
        var sb = new StringBuilder();
        sb.Append(_project).Append('|').Append(_k.Api.SignedIn).Append('|').Append(_k.Error).Append('|').Append(_k.LoadedAt?.ToString("HH:mm"))
          .Append('|').Append(_k.WaitingForGoogle).Append('|').Append(_k.SignInError).Append('|').Append(_doneOpen).Append('|').Append(_quickEpic)
          .Append('|').Append(string.Join(",", s.Shown)).Append(s.ShowDone).Append(s.Compact).Append('|').Append(_card.Current?.Id);
        foreach (var p in _k.Projects) sb.Append(p.Id).Append(p.Name).Append(_k.Progress(p.Id)).Append(';');
        foreach (var e in _k.Epics.Where(e => e.ProjectId == _project)) sb.Append(e.Id).Append(e.Title).Append(e.Status).Append(';');
        foreach (var i in _k.Items.Where(i => i.ProjectId == _project))
            sb.Append(i.Id).Append(i.Status).Append(i.Position).Append(i.Title).Append(i.EpicId).Append(i.Type).Append(i.Archived).Append(i.ClosedAt?.Ticks).Append(';');
        return sb.ToString();
    }

    void BuildProjects()
    {
        _projects.Children.Clear();
        foreach (var p in _k.Projects)
        {
            var on = p.Id == _project;
            var (done, total) = _k.Progress(p.Id);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Text(13, on ? Microsoft.UI.Colors.White : Muted, p.Name));
            var count = Text(11, Faint, $"{done}/{total}");
            Grid.SetColumn(count, 1);
            row.Children.Add(count);
            var b = new Button
            {
                Content = new StackPanel { Spacing = 5, Children = { row, Bar(total == 0 ? 0 : (double)done / total, Theme.Accent) } },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 7, 8, 8),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(on ? Color.FromArgb(0x12, 255, 255, 255) : Microsoft.UI.Colors.Transparent),
            };
            var id = p.Id;
            b.Click += (_, _) =>
            {
                if (id == _project) return;
                _project = id;
                _quickEpic = null;
                _card.Hide();
                _k.RememberProject(id);
                Build(force: true);
            };
            _projects.Children.Add(b);
        }
    }

    UIElement Top(KProject project)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 12, 16, 12), BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Text(18, Microsoft.UI.Colors.White, project.Name);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.MaxWidth = 260;
        grid.Children.Add(title);
        if (_quick.Parent is Panel old) old.Children.Remove(_quick);
        Grid.SetColumn(_quick, 1);
        grid.Children.Add(_quick);
        if (_quickEpic != null && _k.ActiveEpics(project.Id).All(e => e.Id != _quickEpic)) _quickEpic = null;
        var epic = _k.Epic(_quickEpic);
        var pick = new Button
        {
            Content = Text(12, Muted, epic is null ? "без эпика ▾" : $"{epic.Short} ▾"),
            Padding = new Thickness(10, 4, 10, 5),
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(pick, "Эпик новой задачи");
        pick.Click += (_, _) =>
        {
            var flyout = new MenuFlyout();
            var none = new ToggleMenuFlyoutItem { Text = "Без эпика", IsChecked = _quickEpic is null };
            none.Click += (_, _) => { _quickEpic = null; Build(force: true); _quick.Focus(FocusState.Programmatic); };
            flyout.Items.Add(none);
            foreach (var e in _k.ActiveEpics(project.Id))
            {
                var id = e.Id;
                var item = new ToggleMenuFlyoutItem { Text = $"{e.Short} {e.Title}", IsChecked = _quickEpic == id };
                item.Click += (_, _) => { _quickEpic = id; Build(force: true); _quick.Focus(FocusState.Programmatic); };
                flyout.Items.Add(item);
            }
            flyout.ShowAt(pick);
        };
        Grid.SetColumn(pick, 2);
        grid.Children.Add(pick);
        var site = new HyperlinkButton { Content = Text(12, Theme.Accent, "Сайт ↗"), VerticalAlignment = VerticalAlignment.Center };
        site.Click += (_, _) => _k.OpenSite();
        Grid.SetColumn(site, 3);
        grid.Children.Add(site);
        return grid;
    }

    async void AddQuick()
    {
        var title = _quick.Text.Trim();
        if (title.Length == 0 || _project is null || !_quick.IsEnabled) return;
        _quick.IsEnabled = false;
        try
        {
            await _k.Create(_project, title, _quickEpic);
            _quick.Text = "";
        }
        catch (Exception e)
        {
            if (e is not KanbanException) Log.Write($"канбан: задача из окна: {e}");
            _state.Text = e is KanbanException k ? k.Message : "Задача не создалась, подробности в журнале оболочки.";
            _state.Foreground = new SolidColorBrush(Theme.Warning);
        }
        finally
        {
            _quick.IsEnabled = true;
            _quick.Focus(FocusState.Programmatic);
        }
    }

    FrameworkElement Epics(KProject project)
    {
        var grid = new Grid { Padding = new Thickness(16, 10, 16, 10), ColumnSpacing = 14, BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var list = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var e in _k.ActiveEpics(project.Id))
        {
            var (done, total) = _k.Progress(project.Id, e.Id);
            var label = Text(12, Muted, $"{e.Short} {e.Title} · {done}/{total}");
            ToolTipService.SetToolTip(label, $"{e.Id} {e.Title}");
            list.Children.Add(new StackPanel { Width = 170, Spacing = 4, Children = { label, Bar(total == 0 ? 0 : (double)done / total, Violet) } });
        }
        if (list.Children.Count == 0) list.Children.Add(Text(12, Faint, "Активных эпиков нет"));
        grid.Children.Add(new ScrollViewer
        {
            Content = list,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
        });
        var week = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        week.Children.Add(Text(12, Muted, "За неделю сделано"));
        var n = Text(15, Green, _k.DoneThisWeek(project.Id).ToString());
        n.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        week.Children.Add(n);
        Grid.SetColumn(week, 1);
        grid.Children.Add(week);
        return grid;
    }

    FrameworkElement Columns(KProject project, KanbanSettings s)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(16, 12, 16, 12) };
        var shown = s.Shown;
        for (var c = 0; c < shown.Count; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
            var col = ColumnView(project, shown[c], s.Compact);
            Grid.SetColumn(col, c);
            grid.Children.Add(col);
        }
        if (shown.Count == 0) grid.Children.Add(Center("Все колонки выключены в настройках."));
        return grid;
    }

    FrameworkElement ColumnView(KProject project, string status, bool compact)
    {
        var items = _k.Column(project.Id, status);
        var head = new Grid { Padding = new Thickness(4, 2, 4, 2) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(12, Muted, status == KStatus.Waiting ? "Ждёт тебя" : KStatus.Name(status)));
        var count = Text(12, Muted, items.Count.ToString());
        Grid.SetColumn(count, 1);
        head.Children.Add(count);
        var list = new StackPanel { Spacing = 7, Padding = new Thickness(0, 0, 0, 24) };
        foreach (var i in items) list.Children.Add(CardView(i, compact));
        var grid = new Grid { RowSpacing = 7 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(head);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 2, 0) };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        var border = new Border
        {
            Background = new SolidColorBrush(Column),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8),
            Child = grid,
            AllowDrop = true,
        };
        DropTarget(border, list, project.Id, status);
        return border;
    }

    FrameworkElement Done(KProject project, KanbanSettings s)
    {
        var items = _k.Column(project.Id, KStatus.Done);
        var panel = new StackPanel { BorderBrush = new SolidColorBrush(Line), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 4, 16, 8) };
        var toggle = new Button
        {
            Content = Text(12, Faint, $"{(_doneOpen ? "▾" : "▸")} Готово · {items.Count}"),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 4, 8, 4),
        };
        toggle.Click += (_, _) => { _doneOpen = !_doneOpen; Build(force: true); };
        panel.Children.Add(toggle);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, MinHeight = 44 };
        if (_doneOpen)
            foreach (var i in items.Take(40))
            {
                var card = CardView(i, s.Compact);
                card.Width = 220;
                card.Opacity = 0.85;
                row.Children.Add(card);
            }
        var target = new Border
        {
            Child = _doneOpen
                ? new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, MaxHeight = 160 }
                : Text(11, Faint, "Брось сюда карточку — задача готова"),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(4, 0, 4, 2),
            AllowDrop = true,
        };
        // В «Готово» место не выбирается: свежие сверху.
        target.DragOver += (_, e) =>
        {
            if (_drag is null) return;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
            target.Background = new SolidColorBrush(Color.FromArgb(0x14, 0x4C, 0xC2, 0xFF));
        };
        target.DragLeave += (_, _) => target.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        target.Drop += (_, _) =>
        {
            target.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (_drag is not { } item || item.Status == KStatus.Done) return;
            _ = _k.Move(item, KStatus.Done, item.Position);
        };
        panel.Children.Add(target);
        return panel;
    }

    Border CardView(KItem item, bool compact)
    {
        var title = Text(compact ? 12.5 : 13, Microsoft.UI.Colors.White, item.Title);
        title.TextWrapping = TextWrapping.Wrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.MaxLines = compact ? 2 : 4;
        var meta = new Grid();
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (_k.Epic(item.EpicId) is { } epic)
        {
            var tag = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 1, 6, 2),
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0xB3, 0x9C, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = Text(10, Violet, epic.Short),
            };
            ToolTipService.SetToolTip(tag, $"{epic.Id} {epic.Title}");
            meta.Children.Add(tag);
        }
        var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (item.Type == "bug") key.Children.Add(new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 11, Foreground = Theme.Brush(Theme.Danger) });
        if (item.CreatedBy == "claude") ToolTipService.SetToolTip(key, "Создал Claude");
        key.Children.Add(Text(11, Faint, item.Id));
        Grid.SetColumn(key, 1);
        meta.Children.Add(key);
        var selected = _card.Current?.Id == item.Id;
        var card = new Border
        {
            Background = new SolidColorBrush(CardBg),
            BorderBrush = new SolidColorBrush(selected ? Theme.Accent : item.Status == KStatus.Waiting ? Amber : CardLine),
            BorderThickness = selected ? new Thickness(1) : item.Status == KStatus.Waiting ? new Thickness(3, 1, 1, 1) : new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = compact ? new Thickness(8, 5, 8, 5) : new Thickness(9, 8, 9, 8),
            Child = new StackPanel { Spacing = compact ? 3 : 5, Children = { title, meta } },
            CanDrag = true,
            Tag = item,
        };
        card.PointerEntered += (_, _) => card.Background = new SolidColorBrush(CardHover);
        card.PointerExited += (_, _) => card.Background = new SolidColorBrush(CardBg);
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

    List<MenuItem> CardMenu(KItem item)
    {
        var items = new List<MenuItem> { new() { Text = "Открыть", Glyph = "", Invoke = () => { _card.Show(item); Build(force: true); } } };
        items.Add(MenuItem.Sep);
        foreach (var status in KStatus.All.Where(x => x != item.Status))
        {
            var to = status;
            items.Add(new() { Text = $"В «{KStatus.Name(to)}»", Invoke = () => MoveToEnd(item, to) });
        }
        items.Add(MenuItem.Sep);
        items.Add(new() { Text = $"Копировать {item.Id}", Glyph = "", Invoke = () => Copy(item.Id) });
        items.Add(new() { Text = "Копировать номер и название", Glyph = "", Invoke = () => Copy($"{item.Id} {item.Title}") });
        return items;
    }

    void MoveToEnd(KItem item, string status)
    {
        var column = _k.Column(item.ProjectId, status).Where(i => i.Id != item.Id).ToList();
        _ = _k.Move(item, status, KanbanService.PositionAt(column, column.Count));
    }

    static void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    // ---------- перетаскивание ----------

    void DropTarget(Border border, StackPanel list, string project, string status)
    {
        border.DragOver += (_, e) =>
        {
            if (_drag is null) return;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
            ShowGhost(list, IndexAt(list, e.GetPosition(list).Y));
        };
        border.DragLeave += (_, _) => { if (_ghost.Parent == list) RemoveGhost(); };
        border.Drop += (_, e) =>
        {
            var index = IndexAt(list, e.GetPosition(list).Y);
            RemoveGhost();
            if (_drag is not { } item) return;
            var column = _k.Column(project, status).Where(i => i.Id != item.Id).ToList();
            // Бросили туда же, откуда взяли — ничего не меняем.
            if (item.Status == status)
            {
                var was = _k.Column(project, status).FindIndex(i => i.Id == item.Id);
                if (was == index) return;
            }
            _ = _k.Move(item, status, KanbanService.PositionAt(column, index));
        };
    }

    /// Куда встанет карточка: номер среди карточек колонки (без перетаскиваемой).
    int IndexAt(StackPanel list, double y)
    {
        var n = 0;
        foreach (var child in list.Children)
        {
            if (child is not FrameworkElement { Tag: KItem item } el || item.Id == _drag?.Id) continue;
            var top = el.TransformToVisual(list).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (y < top + el.ActualHeight / 2) return n;
            n++;
        }
        return n;
    }

    void ShowGhost(StackPanel list, int index)
    {
        var at = list.Children.Count;
        var n = 0;
        for (var c = 0; c < list.Children.Count; c++)
        {
            if (list.Children[c] is not FrameworkElement { Tag: KItem item } || item.Id == _drag?.Id) continue;
            if (n == index) { at = c; break; }
            n++;
        }
        if (_ghost.Parent == list)
        {
            var cur = list.Children.IndexOf(_ghost);
            if (cur == at || cur == at - 1) return;
        }
        RemoveGhost();
        if (at > list.Children.Count) at = list.Children.Count;
        list.Children.Insert(at, _ghost);
    }

    void RemoveGhost()
    {
        if (_ghost.Parent is Panel p) p.Children.Remove(_ghost);
    }

    // ---------- мелочи ----------

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
        var t = Text(14, Muted, text);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextWrapping = TextWrapping.Wrap;
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
