using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Config;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Start;

/// Свой Пуск (раздел 3): поиск сверху, вкладки категорий и «Все приложения», недавние файлы,
/// внизу пользователь, быстрые папки и питание. Фокус всё время в поле поиска: печать сразу
/// ищет, а стрелки, Tab и Enter ходят по Пуску (выделение видно, только когда работаешь
/// с клавиатуры). Иконки можно перетащить на док (закрепить) и на другую вкладку.
public sealed class StartMenu : Popup
{
    const double W = 680, TileW = 100, TileH = 92, Cols = 6, Spacing = 8, Pad = 20, RowH = 40;
    const string All = "all";

    enum Zone { Tabs, Main, Recent, Bottom, Results, Card }

    sealed class Cell
    {
        public required Border Visual;
        public required Action Invoke;
        public Action<Point?>? Menu;
        /// Что тащить на док или на вкладку.
        public LauncherItem? Drag;
        public string? FromCategory;
        /// Вкладка (для броска на неё).
        public string? Tab;
        public StartEntry? Entry;
        public string? Tip;
    }

    readonly Services _s;
    readonly Catalog _catalog;
    readonly Grid _root = new();
    readonly TextBox _search;
    readonly StackPanel _tabsPanel = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    readonly Grid _main = new();
    readonly Grid _grid = new() { ColumnSpacing = Spacing, RowSpacing = Spacing };
    readonly ScrollViewer _gridScroll;
    readonly StackPanel _allList = new();
    readonly ScrollViewer _allScroll;
    readonly Grid _letters = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock _empty;
    readonly Grid _results = new() { Visibility = Visibility.Collapsed, ColumnSpacing = 12 };
    readonly StackPanel _resultList = new();
    readonly ScrollViewer _resultScroll;
    readonly StackPanel _card = new() { Spacing = 4 };
    readonly StackPanel _recentBox = new() { Spacing = 6 };
    readonly Grid _recentGrid = new() { ColumnSpacing = 8, RowSpacing = 4 };
    readonly Grid _bottom = new();

    readonly Dictionary<Zone, List<Cell>> _cells = [];
    readonly Dictionary<Zone, int> _cols = [];
    Zone _zone = Zone.Main;
    int _sel = -1;
    bool _keyboard;

    string _tab = All;
    double _height = 720;
    List<StartEntry> _recent = [];
    List<StartEntry> _files = [];
    string _filesQuery = "";
    CancellationTokenSource? _filesCancel;
    readonly DispatcherTimer _filesDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    Search.Results? _shownResults;

    // Перетаскивание.
    Cell? _pressCell;
    Pointer? _pointer;
    POINT _pressAt;
    Cell? _dragCell;
    Cell? _dropTab;
    IDropZone? _zoneUnder;
    readonly DragGhost _ghost = new();
    readonly DispatcherTimer _dragPoll = new() { Interval = TimeSpan.FromMilliseconds(16) };

    public StartMenu(Services services, Catalog catalog) : base(activating: true)
    {
        _s = services;
        _catalog = catalog;

        _search = new TextBox
        {
            PlaceholderText = "Поиск программ, параметров и файлов",
            FontFamily = Theme.Font,
            FontSize = 14,
            Height = 36,
            Padding = new Thickness(36, 7, 10, 0),
            CornerRadius = new CornerRadius(18),
        };
        var searchIcon = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 14, Foreground = Theme.Brush(Theme.TextSecondary), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false };
        var searchBox = new Grid { Margin = new Thickness(Pad, Pad, Pad, 14), Children = { _search, searchIcon } };

        var tabsScroll = new ScrollViewer
        {
            Content = _tabsPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Margin = new Thickness(Pad, 0, Pad, 12),
        };

        for (var i = 0; i < Cols; i++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileW) });
        _gridScroll = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(Pad, 0, Pad, 0) };
        _allScroll = new ScrollViewer { Content = _allList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(Pad - 8, 0, Pad - 8, 0), Visibility = Visibility.Collapsed };
        _empty = new TextBlock { FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.TextSecondary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Visibility = Visibility.Collapsed };
        _letters.Background = Theme.Brush(Windows.UI.Color.FromArgb(0xF2, 0x24, 0x24, 0x24));
        _letters.Margin = new Thickness(Pad - 8, 0, Pad - 8, 0);
        _letters.CornerRadius = new CornerRadius(8);
        _main.Children.Add(_gridScroll);
        _main.Children.Add(_allScroll);
        _main.Children.Add(_empty);
        _main.Children.Add(_letters);

        _results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        _results.Margin = new Thickness(Pad - 8, 0, Pad, 0);
        _resultScroll = new ScrollViewer { Content = _resultList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _results.Children.Add(_resultScroll);
        var cardBox = new Border
        {
            Background = Theme.Brush(Theme.Card),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 18, 14, 14),
            Child = _card,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(cardBox, 1);
        _results.Children.Add(cardBox);

        _recentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _recentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _recentBox.Margin = new Thickness(Pad - 8, 10, Pad - 8, 12);
        _recentBox.Children.Add(new TextBlock { Text = "Недавние файлы", FontFamily = Theme.Font, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Text), Margin = new Thickness(8, 0, 0, 0) });
        _recentBox.Children.Add(_recentGrid);

        var bottomBox = new Border
        {
            Background = Theme.Brush(Windows.UI.Color.FromArgb(0x30, 0, 0, 0)),
            BorderBrush = Theme.Brush(Theme.Separator),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Pad - 8, 10, Pad - 8, 10),
            Child = _bottom,
        };

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(tabsScroll, 1);
        Grid.SetRow(_main, 2);
        Grid.SetRow(_results, 2);
        Grid.SetRowSpan(_results, 2);
        Grid.SetRow(_recentBox, 3);
        Grid.SetRow(bottomBox, 4);
        _root.Children.Add(searchBox);
        _root.Children.Add(tabsScroll);
        _root.Children.Add(_main);
        _root.Children.Add(_results);
        _root.Children.Add(_recentBox);
        _root.Children.Add(bottomBox);
        _root.Width = W;
        _root.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
        SetContent(_root);

        _search.TextChanged += (_, _) => OnQuery();
        _search.PreviewKeyDown += OnKey;
        Win.Root.PointerMoved += (_, e) =>
        {
            if (_pressCell != null && _dragCell is null) OnPressMoved();
            if (_keyboard) { _keyboard = false; Paint(); }
        };
        Win.Root.PointerReleased += (_, e) =>
        {
            if (_dragCell != null) { e.Handled = true; FinishDrag(commit: true); }
        };
        _dragPoll.Tick += (_, _) =>
        {
            if (_dragCell is null) { _dragPoll.Stop(); return; }
            if (GetAsyncKeyState(0x1B) < 0) { FinishDrag(commit: false); return; }
            if (GetAsyncKeyState(0x01) >= 0) { FinishDrag(commit: true); return; }
            GetCursorPos(out var c);
            UpdateDrag(c);
        };
        _filesDelay.Tick += (_, _) => { _filesDelay.Stop(); SearchFiles(); };
        _s.Config.Changed += () => { if (IsOpen && _dragCell is null) Refresh(); };
        _catalog.Changed += () => { if (IsOpen && _dragCell is null) Refresh(); };
    }

    protected override double Width => W;
    protected override double Gap => 10;
    protected override double Rise => 12;
    protected override Size Measure(double width) => new(W, _height);

    // ---------- открытие ----------

    /// Открыть над кнопкой Пуска дока (`anchorPx` — таблетка дока на экране).
    public void Show(object anchor, Rect anchorPx, object owner)
    {
        _catalog.Refresh();
        LoadRecent();
        _search.Text = "";
        _files = [];
        _filesQuery = "";
        _keyboard = false;
        _zone = Zone.Main;
        _sel = -1;
        _letters.Visibility = Visibility.Collapsed;
        var start = _s.Config.Start;
        _tab = start.LastTab is { } t && (t == All || _s.Config.CategoryById(t) != null) ? t : _s.Config.Categories.FirstOrDefault()?.Id ?? All;
        // Высота: не больше экрана над доком.
        var mon = MonitorFromPoint(new POINT { X = (int)anchorPx.X + 1, Y = (int)anchorPx.Y }, MONITOR_DEFAULTTONEAREST);
        var scale = MonitorScale(mon);
        var area = MonitorInfo(mon).rcMonitor;
        var room = (anchorPx.Y - area.Top) / scale - Gap - 16;
        _height = Math.Clamp(room, 420, 720);
        _root.Height = _height;
        Refresh();
        Open(anchor, anchorPx, Placement.AboveLeft, owner: owner);
        _search.Focus(FocusState.Programmatic);
    }

    /// Win+S, Win+Q при открытом Пуске: курсор в поиск.
    public void FocusSearch()
    {
        if (!IsOpen) return;
        Native.Win32.SetForegroundWindow(Hwnd);
        _search.Focus(FocusState.Programmatic);
        _search.SelectAll();
    }

    void Refresh()
    {
        BuildTabs();
        BuildMain();
        BuildRecent();
        BuildBottom();
        OnQuery();
    }

    bool Searching => _search.Text.Trim().Length > 0;

    void LoadRecent()
    {
        if (!_s.Config.Start.RecentFiles) { _recent = []; return; }
        var q = _s.Queue;
        Task.Run(() =>
        {
            var list = RecentFiles.Load(6);
            q.TryEnqueue(() =>
            {
                _recent = list;
                if (IsOpen && _dragCell is null) { BuildRecent(); if (Searching) OnQuery(); else Paint(); }
            });
        });
    }

    // ---------- вкладки ----------

    void BuildTabs()
    {
        _tabsPanel.Children.Clear();
        var cells = new List<Cell>();
        foreach (var cat in _s.Config.Categories)
            cells.Add(TabCell(cat.Id, cat.Name, Theme.Parse(cat.Color)));
        cells.Add(TabCell(All, "Все приложения", null));
        foreach (var c in cells) _tabsPanel.Children.Add(c.Visual);
        SetCells(Zone.Tabs, cells, cols: cells.Count);
    }

    Cell TabCell(string id, string name, Windows.UI.Color? color)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        if (color is { } c) content.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Theme.Brush(c), VerticalAlignment = VerticalAlignment.Center });
        else content.Children.Add(new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 12, Foreground = Theme.Brush(Theme.TextSecondary) });
        content.Children.Add(new TextBlock { Text = name, FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center });
        var border = new Border { Height = 32, Padding = new Thickness(12, 0, 14, 0), CornerRadius = new CornerRadius(16), Child = content };
        var cell = new Cell { Visual = border, Invoke = () => SelectTab(id), Tab = id };
        Wire(cell);
        return cell;
    }

    void SelectTab(string id)
    {
        if (_tab == id && !Searching) return;
        _tab = id;
        _letters.Visibility = Visibility.Collapsed;
        // Запоминаем без события «конфиг поменялся»: доки и панели тут ни при чём.
        if (_s.Config.Start.LastTab != id) { _s.Config.Start.LastTab = id; _s.Config.Save(); }
        BuildMain();
        Paint();
    }

    void NextTab(int step)
    {
        var tabs = _cells[Zone.Tabs];
        if (tabs.Count == 0) return;
        var i = tabs.FindIndex(c => c.Tab == _tab);
        SelectTab(tabs[(i + step + tabs.Count) % tabs.Count].Tab!);
    }

    // ---------- содержимое вкладки ----------

    void BuildMain()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _allList.Children.Clear();
        var cells = new List<Cell>();
        _empty.Visibility = Visibility.Collapsed;
        if (_tab == All)
        {
            _gridScroll.Visibility = Visibility.Collapsed;
            _allScroll.Visibility = Visibility.Visible;
            string? group = null;
            foreach (var app in _catalog.Apps)
            {
                var g = Group(app.Title);
                if (g != group)
                {
                    group = g;
                    _allList.Children.Add(LetterHeader(g));
                }
                var cell = AppRow(app);
                _allList.Children.Add(cell.Visual);
                cells.Add(cell);
            }
            if (_catalog.Apps.Count == 0) { _empty.Text = "Список программ загружается…"; _empty.Visibility = Visibility.Visible; }
            SetCells(Zone.Main, cells, cols: 1);
            return;
        }
        _gridScroll.Visibility = Visibility.Visible;
        _allScroll.Visibility = Visibility.Collapsed;
        var cat = _s.Config.CategoryById(_tab);
        var items = cat?.Items ?? [];
        var rows = (int)Math.Ceiling(items.Count / Cols);
        for (var r = 0; r < rows; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileH) });
        for (var i = 0; i < items.Count; i++)
        {
            var cell = Tile(items[i], _tab);
            Grid.SetColumn(cell.Visual, i % (int)Cols);
            Grid.SetRow(cell.Visual, i / (int)Cols);
            _grid.Children.Add(cell.Visual);
            cells.Add(cell);
        }
        if (items.Count == 0)
        {
            _empty.Text = "Здесь пока пусто. Добавить программу: правый клик в «Все приложения» → «Добавить в категорию» или перетащи иконку на эту вкладку";
            _empty.Visibility = Visibility.Visible;
        }
        _gridScroll.ChangeView(null, 0, null, true);
        SetCells(Zone.Main, cells, cols: (int)Cols);
    }

    static string Group(string title)
    {
        var ch = title.Length > 0 ? char.ToUpperInvariant(title[0]) : '#';
        if (ch == 'Ё') ch = 'Е';
        return char.IsLetter(ch) ? ch.ToString() : "#";
    }

    FrameworkElement LetterHeader(string letter)
    {
        var text = new TextBlock { Text = letter, FontFamily = Theme.Font, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Accent), VerticalAlignment = VerticalAlignment.Center };
        var header = new Border { Height = 32, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4), Child = text, Background = Theme.Brush(Microsoft.UI.Colors.Transparent), Tag = letter };
        header.PointerEntered += (_, _) => header.Background = Theme.Brush(Theme.Hover);
        header.PointerExited += (_, _) => header.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
        header.PointerReleased += (_, e) => { e.Handled = true; ShowLetters(); };
        ToolTipService.SetToolTip(header, "Перейти к букве");
        return header;
    }

    /// Сетка из букв: клик прыгает к ней (3.4).
    void ShowLetters()
    {
        var present = _allList.Children.OfType<Border>().Where(b => b.Tag is string).Select(b => (string)b.Tag).ToHashSet();
        var letters = new List<string> { "#" };
        for (var c = 'A'; c <= 'Z'; c++) letters.Add(c.ToString());
        foreach (var c in "АБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЭЮЯ") letters.Add(c.ToString());
        _letters.Children.Clear();
        _letters.ColumnDefinitions.Clear();
        _letters.RowDefinitions.Clear();
        const int cols = 8;
        var wrap = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 4, RowSpacing = 4 };
        for (var i = 0; i < cols; i++) wrap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var rows = (letters.Count + cols - 1) / cols;
        for (var i = 0; i < rows; i++) wrap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        for (var i = 0; i < letters.Count; i++)
        {
            var l = letters[i];
            var on = present.Contains(l);
            var b = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                Child = new TextBlock { Text = l, FontFamily = Theme.Font, FontSize = 15, Foreground = Theme.Brush(on ? Theme.Text : Theme.TextTertiary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            if (on)
            {
                b.PointerEntered += (_, _) => b.Background = Theme.Brush(Theme.Hover);
                b.PointerExited += (_, _) => b.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
                b.PointerReleased += (_, e) => { e.Handled = true; JumpTo(l); };
            }
            Grid.SetColumn(b, i % cols);
            Grid.SetRow(b, i / cols);
            wrap.Children.Add(b);
        }
        _letters.Children.Add(wrap);
        _letters.Visibility = Visibility.Visible;
    }

    void JumpTo(string letter)
    {
        _letters.Visibility = Visibility.Collapsed;
        var header = _allList.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag, letter));
        if (header is null) return;
        var y = header.TransformToVisual(_allList).TransformPoint(new Point(0, 0)).Y;
        _allScroll.ChangeView(null, y, null, true);
        // Выделение — на первую программу этой буквы.
        var index = _cells[Zone.Main].FindIndex(c => c.Entry != null && Group(c.Entry.Title) == letter);
        if (index >= 0) { _zone = Zone.Main; _sel = index; Paint(); }
    }

    Cell AppRow(StartEntry app)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Icon(app.IconSource, "", 24));
        var text = new TextBlock { Text = app.Title, FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var border = new Border { Height = RowH, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4), Child = grid };
        var item = new LauncherItem { Kind = "app", Title = app.Title, Target = app.Target, Meta = "Приложение" };
        var cell = new Cell { Visual = border, Entry = app, Drag = item, Invoke = () => OpenEntry(app), Menu = at => ShowMenu(at, EntryMenu(app, fromAll: true)) };
        Wire(cell);
        return cell;
    }

    Cell Tile(LauncherItem item, string categoryId)
    {
        var glyph = item.Kind switch { "url" => "", "folder" => "", "file" => "", _ => "" };
        var source = item.Kind == "url" ? null : item.Kind == "app" ? Launcher.ShellName(item.Target) : item.Target;
        var title = new TextBlock
        {
            Text = item.Title,
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.Text),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            Margin = new Thickness(4, 6, 4, 0),
        };
        var icon = Icon(source, glyph, 40);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var border = new Border
        {
            Width = TileW,
            Height = TileH,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0, 10, 0, 0),
            Child = new StackPanel { Children = { icon, title } },
            Opacity = Exists(item) ? 1 : 0.4,
        };
        var cell = new Cell
        {
            Visual = border,
            Drag = item,
            FromCategory = categoryId,
            Invoke = () => OpenItem(item),
            Menu = at => ShowMenu(at, TileMenu(item, categoryId)),
            Tip = Exists(item) ? item.Title : $"{item.Title} — не найдено",
        };
        Wire(cell);
        return cell;
    }

    static bool Exists(LauncherItem item) => item.Kind switch
    {
        "url" => true,
        "folder" => Directory.Exists(item.Target),
        "file" => File.Exists(item.Target),
        _ => Launcher.Resolve(item.Target).Exists,
    };

    // ---------- недавние ----------

    void BuildRecent()
    {
        _recentGrid.Children.Clear();
        _recentGrid.RowDefinitions.Clear();
        var cells = new List<Cell>();
        var on = _s.Config.Start.RecentFiles;
        if (on)
        {
            var rows = (_recent.Count + 1) / 2;
            for (var r = 0; r < rows; r++) _recentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            for (var i = 0; i < _recent.Count; i++)
            {
                var f = _recent[i];
                var grid = new Grid { ColumnSpacing = 10 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.Children.Add(Icon(f.IconSource, "", 28));
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(new TextBlock { Text = f.Title, FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), TextTrimming = TextTrimming.CharacterEllipsis });
                texts.Children.Add(new TextBlock { Text = f.Subtitle, FontFamily = Theme.Font, FontSize = 11, Foreground = Theme.Brush(Theme.TextSecondary), TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(texts, 1);
                grid.Children.Add(texts);
                var border = new Border { Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(4), Child = grid };
                var file = f;
                var item = new LauncherItem { Kind = "file", Title = f.Title, Target = f.Target, Meta = "Файл" };
                var cell = new Cell
                {
                    Visual = border,
                    Entry = f,
                    Drag = item,
                    Invoke = () => OpenEntry(file),
                    Menu = at => ShowMenu(at,
                    [
                        new() { Text = "Открыть", Glyph = "", Invoke = () => OpenEntry(file) },
                        new() { Text = "Открыть расположение", Glyph = "", Invoke = () => { Close(restoreFocus: false); Launcher.Reveal(file.Target); } },
                        MenuItem.Sep,
                        new() { Text = "Убрать из списка", Glyph = "", Invoke = () => { RecentFiles.Remove(file); _recent.Remove(file); BuildRecent(); Paint(); } },
                    ]),
                    Tip = f.Target,
                };
                Wire(cell);
                Grid.SetColumn(border, i % 2);
                Grid.SetRow(border, i / 2);
                _recentGrid.Children.Add(border);
                cells.Add(cell);
            }
        }
        _recentBox.Visibility = on && _recent.Count > 0 && !Searching ? Visibility.Visible : Visibility.Collapsed;
        SetCells(Zone.Recent, cells, cols: 2);
    }

    // ---------- нижняя строка ----------

    static readonly Dictionary<string, (string Title, string Glyph, string Target)> Folders = new()
    {
        ["explorer"] = ("Проводник", "", "explorer.exe"),
        ["documents"] = ("Документы", "", "shell:Personal"),
        ["downloads"] = ("Загрузки", "", "shell:Downloads"),
        ["pictures"] = ("Изображения", "", "shell:My Pictures"),
        ["music"] = ("Музыка", "", "shell:My Music"),
        ["videos"] = ("Видео", "", "shell:My Video"),
        ["settings"] = ("Параметры", "", "ms-settings:"),
    };

    /// Быстрые папки, которые можно выбрать (меню трея → «Пуск»).
    public static IEnumerable<(string Id, string Title)> FolderChoices => Folders.Select(f => (f.Key, f.Value.Title));

    void BuildBottom()
    {
        _bottom.Children.Clear();
        _bottom.ColumnDefinitions.Clear();
        _bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cells = new List<Cell>();

        // Пользователь.
        var name = UserInfo.Name;
        var avatar = new Grid { Width = 32, Height = 32 };
        var circle = new Ellipse { Width = 32, Height = 32, Fill = Theme.Brush(Theme.Accent) };
        avatar.Children.Add(circle);
        if (UserInfo.Picture is { } pic)
        {
            try { circle.Fill = new ImageBrush { ImageSource = new BitmapImage(new Uri(pic)), Stretch = Stretch.UniformToFill }; }
            catch { }
        }
        else
        {
            avatar.Children.Add(new TextBlock { Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?", FontFamily = Theme.Font, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush(Windows.UI.Color.FromArgb(255, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
        var user = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        user.Children.Add(avatar);
        user.Children.Add(new TextBlock { Text = name, FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 });
        var userBorder = new Border { Height = 44, Padding = new Thickness(8, 0, 12, 0), CornerRadius = new CornerRadius(6), Child = user, HorizontalAlignment = HorizontalAlignment.Left };
        Cell? userCell = null;
        userCell = new Cell { Visual = userBorder, Invoke = () => ShowMenu(AboveOf(userCell!), UserMenu()), Tip = "Учётная запись" };
        userCell.Menu = at => userCell.Invoke();
        Wire(userCell);
        _bottom.Children.Add(userBorder);
        cells.Add(userCell);

        // Быстрые папки и питание.
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        Grid.SetColumn(right, 1);
        _bottom.Children.Add(right);
        foreach (var id in _s.Config.Start.Folders)
        {
            if (!Folders.TryGetValue(id, out var f)) continue;
            var target = f.Target;
            var cell = IconButton(f.Glyph, f.Title, () => { Close(restoreFocus: false); Launcher.Open(target); });
            right.Children.Add(cell.Visual);
            cells.Add(cell);
        }
        Cell? power = null;
        power = IconButton("", "Питание", () => ShowMenu(AboveOf(power!), PowerMenu()));
        power.Menu = _ => power.Invoke();
        right.Children.Add(power.Visual);
        cells.Add(power);
        SetCells(Zone.Bottom, cells, cols: cells.Count);
    }

    Cell IconButton(string glyph, string tip, Action invoke)
    {
        var border = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(6),
            Child = new FontIcon { Glyph = glyph, FontFamily = Theme.Icons, FontSize = 16, Foreground = Theme.Brush(Theme.Text) },
        };
        var cell = new Cell { Visual = border, Invoke = invoke, Tip = tip };
        Wire(cell);
        return cell;
    }

    List<MenuItem> UserMenu() =>
    [
        new() { Text = "Изменить параметры учётной записи", Glyph = "", Invoke = () => { Close(restoreFocus: false); Launcher.Open("ms-settings:yourinfo"); } },
        MenuItem.Sep,
        new() { Text = "Заблокировать", Glyph = "", Invoke = () => { Close(restoreFocus: false); Power.Lock(); } },
        new() { Text = "Выйти", Glyph = "", Invoke = () => { Close(restoreFocus: false); Power.SignOut(); } },
        new() { Text = "Сменить пользователя", Glyph = "", Invoke = () => { Close(restoreFocus: false); Power.SwitchUser(); } },
    ];

    /// Меню питания: то же в Пуске и в «Завершение работы или выход» меню Win+X.
    public static List<MenuItem> PowerMenu(bool signOut = false, Action? before = null)
    {
        var updates = Power.UpdatesPending;
        void Do(Action a) { before?.Invoke(); a(); }
        var items = new List<MenuItem>();
        if (signOut) items.Add(new() { Text = "Выйти", Glyph = "", Key = 'I', Invoke = () => Do(Power.SignOut) });
        items.Add(new() { Text = "Заблокировать", Glyph = "", Key = signOut ? null : 'L', Invoke = () => Do(Power.Lock) });
        items.Add(new() { Text = "Спящий режим", Glyph = "", Key = 'S', Invoke = () => Do(Power.Sleep) });
        items.Add(new() { Text = updates ? "Обновить и завершить работу" : "Завершение работы", Glyph = "", Key = 'U', Invoke = () => Do(() => Power.Shutdown(updates)) });
        items.Add(new() { Text = updates ? "Обновить и перезагрузить" : "Перезагрузка", Glyph = "", Key = 'R', Invoke = () => Do(() => Power.Restart(updates)) });
        return items;
    }

    Point AboveOf(Cell cell)
    {
        GetWindowRect(Hwnd, out var r);
        var o = cell.Visual.TransformToVisual(null).TransformPoint(new Point(0, 0));
        return new Point(r.Left + o.X * Win.Scale, r.Top + o.Y * Win.Scale);
    }

    // ---------- поиск ----------

    int Usage(StartEntry e) => _s.Config.Start.Launches.TryGetValue(e.Target.ToLowerInvariant(), out var n) ? n : 0;

    void OnQuery()
    {
        var searching = Searching;
        _results.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        _main.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        _recentBox.Visibility = !searching && _s.Config.Start.RecentFiles && _recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!searching)
        {
            _filesCancel?.Cancel();
            _filesDelay.Stop();
            _files = [];
            _filesQuery = "";
            _shownResults = null;
            if (_zone is Zone.Results or Zone.Card) { _zone = Zone.Main; _sel = -1; }
            Paint();
            return;
        }
        var q = _search.Text.Trim();
        // Старые найденные файлы держатся, пока не пришли новые (без мигания).
        var files = _filesQuery.Length > 0 ? _files.Where(f => Search.Score(f.Title, q.ToLowerInvariant().Replace('ё', 'е')) > 0).ToList() : [];
        ShowResults(Search.Instant(_catalog, q, _recent, files, Usage));
        _filesDelay.Stop();
        _filesDelay.Start();
    }

    void SearchFiles()
    {
        var q = _search.Text.Trim();
        if (q.Length < 2) return;
        _filesCancel?.Cancel();
        var cts = new CancellationTokenSource();
        _filesCancel = cts;
        var queue = _s.Queue;
        Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var found = Search.Files(q, 6, cts.Token);
            if (cts.IsCancellationRequested) return;
            queue.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested || _search.Text.Trim() != q || !IsOpen) return;
                _files = found;
                _filesQuery = q;
                ShowResults(Search.Instant(_catalog, q, _recent, found, Usage));
                Log.Write($"Пуск: файлов «{q}» {found.Count} за {sw.ElapsedMilliseconds} мс");
            });
        });
    }

    void ShowResults(Search.Results r)
    {
        // Выделенная строка остаётся выделенной, если она ещё в результатах.
        var keep = _zone == Zone.Results && _sel >= 0 && _sel < (_cells.GetValueOrDefault(Zone.Results)?.Count ?? 0) ? _cells[Zone.Results][_sel].Entry : null;
        _shownResults = r;
        _resultList.Children.Clear();
        var cells = new List<Cell>();
        void Section(string title, IReadOnlyList<StartEntry> items)
        {
            if (items.Count == 0) return;
            _resultList.Children.Add(new TextBlock { Text = title, FontFamily = Theme.Font, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush(Theme.TextSecondary), Margin = new Thickness(10, cells.Count == 0 ? 0 : 10, 0, 4) });
            foreach (var e in items)
            {
                var cell = ResultRow(e, big: title == "Лучшее совпадение");
                _resultList.Children.Add(cell.Visual);
                cells.Add(cell);
            }
        }
        if (r.Best is { } best) Section("Лучшее совпадение", [best]);
        Section("Приложения", r.Apps);
        Section("Настройки", r.Settings);
        Section("Файлы", r.Files);
        Section("Команды", r.Commands);
        if (r.Empty)
        {
            var q = _search.Text.Trim();
            _resultList.Children.Add(new TextBlock { Text = "Ничего не найдено", FontFamily = Theme.Font, FontSize = 14, Foreground = Theme.Brush(Theme.Text), Margin = new Thickness(10, 6, 0, 10) });
            var web = new StartEntry(ResultKind.Web, $"Искать «{q}» в интернете", "https://www.google.com/search?q=" + Uri.EscapeDataString(q), "Откроется браузер") { Glyph = "" };
            var cell = ResultRow(web, big: false);
            _resultList.Children.Add(cell.Visual);
            cells.Add(cell);
        }
        SetCells(Zone.Results, cells, cols: 1);
        if (_zone is not (Zone.Results or Zone.Card)) _zone = Zone.Results;
        if (_zone == Zone.Results)
        {
            var i = keep is null ? -1 : cells.FindIndex(c => Equals(c.Entry, keep));
            _sel = i >= 0 ? i : cells.Count > 0 ? 0 : -1;
        }
        BuildCard();
        Paint();
    }

    Cell ResultRow(StartEntry e, bool big)
    {
        var size = big ? 32 : 24;
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Icon(e.IconSource, e.Glyph ?? DefaultGlyph(e.Kind), size));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = e.Title, FontFamily = Theme.Font, FontSize = big ? 14 : 13, Foreground = Theme.Brush(Theme.Text), TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = KindText(e), FontFamily = Theme.Font, FontSize = 11, Foreground = Theme.Brush(Theme.TextSecondary), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var border = new Border { Height = big ? 54 : 44, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4), Child = grid };
        LauncherItem? drag = e.Kind switch
        {
            ResultKind.App => new LauncherItem { Kind = "app", Title = e.Title, Target = e.Target, Meta = "Приложение" },
            ResultKind.File => ShellConfig.ItemFromPath(e.Target),
            ResultKind.Folder => ShellConfig.ItemFromPath(e.Target),
            _ => null,
        };
        var cell = new Cell { Visual = border, Entry = e, Drag = drag, Invoke = () => OpenEntry(e), Menu = at => ShowMenu(at, EntryMenu(e, fromAll: false)) };
        Wire(cell);
        return cell;
    }

    static string DefaultGlyph(ResultKind k) => k switch
    {
        ResultKind.Setting => "",
        ResultKind.File => "",
        ResultKind.Folder => "",
        ResultKind.Command => "",
        ResultKind.Web => "",
        _ => "",
    };

    static string KindText(StartEntry e) => e.Kind switch
    {
        ResultKind.App => "Приложение",
        ResultKind.Setting => "Параметры",
        ResultKind.File or ResultKind.Folder => e.Subtitle,
        ResultKind.Command => e.Subtitle == e.Target ? "Команда" : $"Команда · {e.Subtitle}",
        _ => e.Subtitle,
    };

    StartEntry? _cardFor;

    /// Карточка справа: большая иконка, название и действия (3.3).
    void BuildCard()
    {
        _card.Children.Clear();
        var rc = _cells.GetValueOrDefault(Zone.Results) ?? [];
        var e = _zone == Zone.Card ? _cardFor : (_sel >= 0 && _sel < rc.Count ? rc[_sel].Entry : null);
        _cardFor = e;
        var cells = new List<Cell>();
        if (e is null) { SetCells(Zone.Card, cells, cols: 1); return; }
        var icon = Icon(e.IconSource, e.Glyph ?? DefaultGlyph(e.Kind), 64);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 0, 10);
        _card.Children.Add(icon);
        _card.Children.Add(new TextBlock { Text = e.Title, FontFamily = Theme.Font, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Text), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        _card.Children.Add(new TextBlock { Text = KindText(e), FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextSecondary), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 12) });
        _card.Children.Add(new Rectangle { Height = 1, Fill = Theme.Brush(Theme.Separator), Margin = new Thickness(0, 0, 0, 6) });
        foreach (var a in Actions(e))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon { Glyph = a.Glyph ?? "", FontFamily = Theme.Icons, FontSize = 14, Foreground = Theme.Brush(Theme.Text) });
            row.Children.Add(new TextBlock { Text = a.Text, FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(a.Enabled ? Theme.Text : Theme.TextTertiary), VerticalAlignment = VerticalAlignment.Center });
            var border = new Border { Height = 36, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(4), Child = row };
            var action = a;
            Cell? cell = null;
            cell = new Cell
            {
                Visual = border,
                Invoke = () =>
                {
                    if (!action.Enabled) return;
                    if (action.Children != null) ShowMenu(AboveOf(cell!) is var p ? new Point(p.X, p.Y + border.ActualHeight * Win.Scale) : null, action.Children);
                    else action.Invoke?.Invoke();
                },
            };
            Wire(cell);
            _card.Children.Add(border);
            cells.Add(cell);
        }
        SetCells(Zone.Card, cells, cols: 1);
    }

    /// Действия с найденным: те же в карточке и в правом клике.
    List<MenuItem> Actions(StartEntry e)
    {
        var list = new List<MenuItem> { new() { Text = "Открыть", Glyph = "", Invoke = () => OpenEntry(e) } };
        if (e.Kind is ResultKind.App or ResultKind.Command or ResultKind.File)
            list.Add(new() { Text = "От имени администратора", Glyph = "", Invoke = () => OpenEntry(e, admin: true) });
        var location = Location(e);
        if (e.Kind is ResultKind.App or ResultKind.Command or ResultKind.File or ResultKind.Folder)
            list.Add(new() { Text = "Открыть расположение", Glyph = "", Enabled = location != null, Invoke = () => { if (location != null) { Close(restoreFocus: false); Launcher.Reveal(location); } } });
        if (e.Kind == ResultKind.App)
        {
            var pinned = _s.Config.Pins.Any(p => string.Equals(p.Target, e.Target, StringComparison.OrdinalIgnoreCase));
            list.Add(new() { Text = pinned ? "Уже в доке" : "Закрепить в доке", Glyph = "", Enabled = !pinned, Invoke = () => _s.PinApp(e.Title, e.Target) });
        }
        if (e.Kind is ResultKind.App or ResultKind.File or ResultKind.Folder)
        {
            var item = e.Kind == ResultKind.App ? new LauncherItem { Kind = "app", Title = e.Title, Target = e.Target, Meta = "Приложение" } : ShellConfig.ItemFromPath(e.Target);
            list.Add(CategoryChoice(item));
        }
        return list;
    }

    MenuItem CategoryChoice(LauncherItem item)
    {
        var cats = _s.Config.Categories;
        return new MenuItem
        {
            Text = "Добавить в категорию",
            Glyph = "",
            Enabled = cats.Count > 0,
            Children = cats.Select(c => new MenuItem
            {
                Text = c.Name,
                Checked = c.Items.Any(i => string.Equals(i.Target, item.Target, StringComparison.OrdinalIgnoreCase)),
                Invoke = () => ToggleCategory(c.Id, item),
            }).ToList(),
        };
    }

    void ToggleCategory(string categoryId, LauncherItem item)
    {
        var cat = _s.Config.CategoryById(categoryId);
        if (cat is null) return;
        var existing = cat.Items.FirstOrDefault(i => string.Equals(i.Target, item.Target, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _s.Config.Update(_ => cat.Items.Remove(existing));
        else _s.AddToCategory(categoryId, [new LauncherItem { Kind = item.Kind, Title = item.Title, Target = item.Target, Meta = item.Meta }]);
    }

    List<MenuItem> EntryMenu(StartEntry e, bool fromAll)
    {
        var items = Actions(e);
        if (fromAll && e.Kind == ResultKind.App)
        {
            items.Add(MenuItem.Sep);
            items.Add(new() { Text = "Удалить программу", Glyph = "", Danger = true, Invoke = () => { Close(restoreFocus: false); Launcher.Open("ms-settings:appsfeatures"); } });
        }
        return items;
    }

    List<MenuItem> TileMenu(LauncherItem item, string categoryId)
    {
        var items = new List<MenuItem> { new() { Text = "Открыть", Glyph = "", Invoke = () => OpenItem(item) } };
        if (item.Kind is "app" or "file")
            items.Add(new() { Text = "От имени администратора", Glyph = "", Invoke = () => OpenItem(item, admin: true) });
        if (item.Kind != "url")
        {
            var path = item.Kind == "app" && !Launcher.IsPath(item.Target) ? Launcher.Resolve(item.Target).Exe : item.Target;
            items.Add(new() { Text = "Открыть расположение", Glyph = "", Enabled = path != null && (File.Exists(path) || Directory.Exists(path)), Invoke = () => { if (path != null) { Close(restoreFocus: false); Launcher.Reveal(path); } } });
        }
        if (item.Kind == "app")
        {
            var pinned = _s.Config.Pins.Any(p => string.Equals(p.Target, item.Target, StringComparison.OrdinalIgnoreCase));
            items.Add(new() { Text = pinned ? "Уже в доке" : "Закрепить в доке", Glyph = "", Enabled = !pinned, Invoke = () => _s.PinApp(item.Title, item.Target) });
        }
        items.Add(CategoryChoice(item));
        items.Add(MenuItem.Sep);
        items.Add(new() { Text = "Убрать из категории", Glyph = "", Danger = true, Invoke = () => _s.Config.Update(cfg => cfg.Categories.FirstOrDefault(x => x.Id == categoryId)?.Items.Remove(item)) });
        return items;
    }

    static string? Location(StartEntry e) => e.Kind switch
    {
        ResultKind.App => Launcher.IsPath(e.Target) ? e.Target : Launcher.Resolve(e.Target).Exe is { } exe && File.Exists(exe) ? exe : null,
        ResultKind.File or ResultKind.Folder => e.Target,
        ResultKind.Command => Catalog.CommandPath(e.Target),
        _ => null,
    };

    // ---------- запуск ----------

    void NoteLaunch(string target)
    {
        var key = target.ToLowerInvariant();
        _s.Config.Update(c =>
        {
            c.Start.Launches[key] = c.Start.Launches.GetValueOrDefault(key) + 1;
            // Счётчиков не больше 300: редкое забывается.
            if (c.Start.Launches.Count > 300)
                foreach (var k in c.Start.Launches.OrderBy(x => x.Value).Take(50).Select(x => x.Key).ToList()) c.Start.Launches.Remove(k);
        });
    }

    void OpenEntry(StartEntry e, bool admin = false)
    {
        // Пуск закрывается сразу (3.1), запуск — в фоне.
        Close(restoreFocus: false);
        switch (e.Kind)
        {
            case ResultKind.App:
                Launcher.LaunchInBackground(e.Target, admin);
                _s.NoteLaunch(e.Target);
                break;
            case ResultKind.Command:
                Launcher.Run(e.Target, e.Args, admin);
                break;
            case ResultKind.File when admin:
                Launcher.Run(e.Target, null, admin: true);
                break;
            default:
                Launcher.Open(e.Target);
                break;
        }
        if (e.Kind != ResultKind.Web) NoteLaunch(e.Target);
    }

    void OpenItem(LauncherItem item, bool admin = false)
    {
        Close(restoreFocus: false);
        if (item.Kind == "app") { Launcher.LaunchInBackground(item.Target, admin); _s.NoteLaunch(item.Target); }
        else if (admin) Launcher.Run(item.Target, null, admin: true);
        else Launcher.Open(item.Target);
    }

    // ---------- ячейки и клавиатура ----------

    void SetCells(Zone zone, List<Cell> cells, int cols)
    {
        _cells[zone] = cells;
        _cols[zone] = Math.Max(1, cols);
        if (zone == _zone && _sel >= cells.Count) _sel = cells.Count - 1;
    }

    /// Мышь: наведение, клик, правый клик, перетаскивание.
    void Wire(Cell cell)
    {
        var b = cell.Visual;
        b.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
        b.BorderThickness = new Thickness(1);
        b.BorderBrush = Theme.Brush(Microsoft.UI.Colors.Transparent);
        if (cell.Tip != null) ToolTipService.SetToolTip(b, cell.Tip);
        b.PointerEntered += (_, _) => { if (_dragCell is null) { _hoverCell = cell; Paint(); } };
        b.PointerExited += (_, _) => { if (_hoverCell == cell) { _hoverCell = null; Paint(); } };
        b.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            var p = e.GetCurrentPoint(b);
            if (!p.Properties.IsLeftButtonPressed) return;
            _pressCell = cell;
            _pointer = e.Pointer;
            GetCursorPos(out _pressAt);
            b.Background = Theme.Brush(Theme.Pressed);
        };
        b.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            if (_dragCell != null) { FinishDrag(commit: true); return; }
            var kind = e.GetCurrentPoint(b).Properties.PointerUpdateKind;
            var pressed = _pressCell;
            _pressCell = null;
            if (kind == PointerUpdateKind.LeftButtonReleased && pressed == cell) cell.Invoke();
            else if (kind == PointerUpdateKind.RightButtonReleased && cell.Menu != null) { GetCursorPos(out var c); cell.Menu(new Point(c.X, c.Y)); }
            Paint();
        };
    }

    Cell? _hoverCell;

    /// Перекрасить выделение: наведение мышью, выделение клавиатурой, выбранная вкладка.
    void Paint()
    {
        foreach (var (zone, cells) in _cells)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                var selected = zone == _zone && i == _sel;
                var tabOn = zone == Zone.Tabs && c.Tab == _tab;
                // Выделение видно с клавиатуры; в результатах поиска первое совпадение выделено всегда.
                var showSel = selected && (_keyboard || zone == Zone.Results);
                var bg = showSel ? Theme.Selected : c == _hoverCell ? Theme.Hover : tabOn ? Theme.Selected : Microsoft.UI.Colors.Transparent;
                c.Visual.Background = Theme.Brush(bg);
                c.Visual.BorderBrush = Theme.Brush(selected && _keyboard ? Windows.UI.Color.FromArgb(0xB0, 255, 255, 255) : tabOn && zone == Zone.Tabs ? Theme.Stroke : Microsoft.UI.Colors.Transparent);
                if (_dropTab == c) c.Visual.Background = Theme.Brush(Windows.UI.Color.FromArgb(0x40, 0x4C, 0xC2, 0xFF));
            }
        }
    }

    Zone[] Order => Searching ? [Zone.Results, Zone.Card, Zone.Bottom] : [Zone.Tabs, Zone.Main, Zone.Recent, Zone.Bottom];

    List<Cell> Cells(Zone z) => _cells.GetValueOrDefault(z) ?? [];

    bool Usable(Zone z) => Cells(z).Count > 0 && (z != Zone.Recent || _recentBox.Visibility == Visibility.Visible);

    void Go(Zone zone, int index)
    {
        _zone = zone;
        var cells = Cells(zone);
        _sel = cells.Count == 0 ? -1 : Math.Clamp(index, 0, cells.Count - 1);
        if (zone == Zone.Tabs && _sel >= 0) SelectTab(cells[_sel].Tab!);
        if (zone == Zone.Results) BuildCard();
        Paint();
        if (_sel >= 0) Cells(_zone)[_sel].Visual.StartBringIntoView();
    }

    void StepZone(int step)
    {
        var order = Order;
        var i = Array.IndexOf(order, _zone);
        for (var n = 0; n < order.Length; n++)
        {
            i = (i + step + order.Length) % order.Length;
            if (!Usable(order[i])) continue;
            var start = order[i] == Zone.Tabs ? Cells(Zone.Tabs).FindIndex(c => c.Tab == _tab) : 0;
            Go(order[i], start);
            return;
        }
    }

    /// Стрелки: внутри зоны по сетке, за краем — в соседнюю зону сверху или снизу.
    void Arrow(int dx, int dy)
    {
        var cells = Cells(_zone);
        if (_sel < 0 || cells.Count == 0)
        {
            var first = Order.FirstOrDefault(Usable, _zone);
            if (first == Zone.Tabs && Usable(Zone.Main)) first = Zone.Main;
            Go(first, 0);
            return;
        }
        var cols = _cols[_zone];
        if (dx != 0)
        {
            var next = _sel + dx;
            if (_zone == Zone.Results) return;
            if (next >= 0 && next < cells.Count) Go(_zone, next);
            return;
        }
        var target = _sel + dy * cols;
        if (target >= 0 && target < cells.Count) { Go(_zone, target); return; }
        // Последняя неполная строка сетки: вниз — на последнюю ячейку.
        if (dy > 0 && _sel / cols < (cells.Count - 1) / cols) { Go(_zone, cells.Count - 1); return; }
        var order = Order;
        var i = Array.IndexOf(order, _zone);
        for (i += dy; i >= 0 && i < order.Length; i += dy)
        {
            if (order[i] == Zone.Card || !Usable(order[i])) continue;
            var z = order[i];
            var count = Cells(z).Count;
            var col = _zone == Zone.Tabs ? 0 : _sel % cols;
            var index = z == Zone.Tabs ? Cells(Zone.Tabs).FindIndex(c => c.Tab == _tab)
                : dy > 0 ? Math.Min(col, count - 1) : Math.Min(count - 1, (count - 1) / _cols[z] * _cols[z] + Math.Min(col, _cols[z] - 1));
            Go(z, index);
            return;
        }
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
        var shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
        var caretAtEnd = _search.SelectionStart >= _search.Text.Length && _search.SelectionLength == 0;
        switch (e.Key)
        {
            case VirtualKey.Escape when _dragCell != null:
                FinishDrag(commit: false);
                break;
            case VirtualKey.Escape when _letters.Visibility == Visibility.Visible:
                _letters.Visibility = Visibility.Collapsed;
                break;
            case VirtualKey.Escape when _search.Text.Length > 0:
                // Сначала очищает поиск, ещё один Esc закрывает Пуск (3.1).
                _search.Text = "";
                break;
            case VirtualKey.Escape:
                Close(restoreFocus: true);
                break;
            case VirtualKey.Tab when ctrl:
                if (!Searching) NextTab(shift ? -1 : 1);
                break;
            case VirtualKey.Tab:
                _keyboard = true;
                StepZone(shift ? -1 : 1);
                break;
            case VirtualKey.Enter:
                OnEnter(ctrl, shift);
                break;
            case VirtualKey.Down:
                _keyboard = true;
                Arrow(0, 1);
                break;
            case VirtualKey.Up:
                _keyboard = true;
                Arrow(0, -1);
                break;
            // В поле с текстом ← → двигают курсор; в результатах → у конца текста — к карточке.
            case VirtualKey.Right when Searching && _zone == Zone.Results:
                if (!caretAtEnd) return;
                _keyboard = true;
                if (Usable(Zone.Card)) Go(Zone.Card, 0);
                break;
            case VirtualKey.Left when Searching && _zone == Zone.Card:
                _keyboard = true;
                Go(Zone.Results, Math.Max(0, Cells(Zone.Results).FindIndex(c => Equals(c.Entry, _cardFor))));
                break;
            case VirtualKey.Left when Searching && _zone == Zone.Results:
            case VirtualKey.Right when Searching && _zone == Zone.Results:
                return;
            case VirtualKey.Right:
                _keyboard = true;
                Arrow(1, 0);
                break;
            case VirtualKey.Left:
                _keyboard = true;
                Arrow(-1, 0);
                break;
            case VirtualKey.Home when !Searching && _zone == Zone.Main:
                _keyboard = true;
                Go(Zone.Main, 0);
                break;
            case VirtualKey.End when !Searching && _zone == Zone.Main:
                _keyboard = true;
                Go(Zone.Main, Cells(Zone.Main).Count - 1);
                break;
            case VirtualKey.Application:
            case VirtualKey.F10 when shift:
                if (_sel >= 0 && _sel < Cells(_zone).Count && Cells(_zone)[_sel] is { Menu: { } menu } c) menu(AboveOf(c) is var p ? new Point(p.X + 24 * Win.Scale, p.Y + 24 * Win.Scale) : null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void OnEnter(bool ctrl, bool shift)
    {
        var cells = Cells(_zone);
        Cell? cell = _sel >= 0 && _sel < cells.Count ? cells[_sel] : null;
        // В поиске без выделения — первое совпадение.
        if (cell is null && Searching && Cells(Zone.Results).Count > 0) cell = Cells(Zone.Results)[0];
        if (cell is null) return;
        if (cell.Entry is { } e && _zone is Zone.Results or Zone.Main or Zone.Recent)
        {
            if (ctrl && shift) { OpenEntry(e, admin: true); return; }
            if (ctrl) { if (Location(e) is { } loc) { Close(restoreFocus: false); Launcher.Reveal(loc); } return; }
        }
        else if (cell.Drag is { } item && _zone == Zone.Main)
        {
            if (ctrl && shift) { OpenItem(item, admin: true); return; }
            if (ctrl && item.Kind != "url")
            {
                var path = item.Kind == "app" && !Launcher.IsPath(item.Target) ? Launcher.Resolve(item.Target).Exe : item.Target;
                if (path != null) { Close(restoreFocus: false); Launcher.Reveal(path); }
                return;
            }
        }
        cell.Invoke();
    }

    void ShowMenu(Point? at, IReadOnlyList<MenuItem> items)
    {
        if (at is not { } p) { GetCursorPos(out var c); p = new Point(c.X, c.Y); }
        _s.Menu.ShowInside(this, (this, "menu", items.Count), new Rect(p.X, p.Y, 0, 0), items);
    }

    // ---------- перетаскивание ----------

    void OnPressMoved()
    {
        if (_pressCell?.Drag is null) return;
        if (GetAsyncKeyState(0x01) >= 0) { _pressCell = null; return; }
        GetCursorPos(out var c);
        if (Math.Abs(c.X - _pressAt.X) + Math.Abs(c.Y - _pressAt.Y) < 6 * Win.Scale) return;
        _dragCell = _pressCell;
        _pressCell = null;
        if (_pointer != null) Win.Root.CapturePointer(_pointer);
        _ghost.Show(FindImage(_dragCell.Visual), c.X, c.Y);
        _dragCell.Visual.Opacity = 0.35;
        _dragPoll.Start();
    }

    static ImageSource? FindImage(DependencyObject root)
    {
        if (root is Image { Source: { } src }) return src;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++) if (FindImage(VisualTreeHelper.GetChild(root, i)) is { } s) return s;
        return null;
    }

    bool InsideWindow(POINT c) => GetWindowRect(Hwnd, out var r) && c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom;

    Cell? TabAt(POINT c)
    {
        GetWindowRect(Hwnd, out var r);
        var p = new Point((c.X - r.Left) / Win.Scale, (c.Y - r.Top) / Win.Scale);
        foreach (var t in Cells(Zone.Tabs))
        {
            var o = t.Visual.TransformToVisual(null).TransformPoint(new Point(0, 0));
            if (p.X >= o.X && p.X < o.X + t.Visual.ActualWidth && p.Y >= o.Y && p.Y < o.Y + t.Visual.ActualHeight) return t;
        }
        return null;
    }

    void UpdateDrag(POINT c)
    {
        _ghost.MoveTo(c.X, c.Y);
        var drag = _dragCell!;
        if (InsideWindow(c))
        {
            _zoneUnder?.DragLeave();
            _zoneUnder = null;
            var tab = TabAt(c);
            // На свою же вкладку и на «Все приложения» бросать нечего.
            if (tab != null && (tab.Tab == All || tab.Tab == drag.FromCategory)) tab = null;
            if (tab != _dropTab) { _dropTab = tab; Paint(); }
            return;
        }
        if (_dropTab != null) { _dropTab = null; Paint(); }
        var payload = new DragPayload(drag.Drag!, drag.FromCategory);
        IDropZone? hit = null;
        foreach (var z in _s.DropZones)
        {
            if (hit is null && z.DragOver(c.X, c.Y, payload)) hit = z;
            else z.DragLeave();
        }
        _zoneUnder = hit;
    }

    void FinishDrag(bool commit)
    {
        _dragPoll.Stop();
        var drag = _dragCell;
        _dragCell = null;
        _ghost.Hide();
        Win.Root.ReleasePointerCaptures();
        if (drag is null) return;
        drag.Visual.Opacity = 1;
        var tab = _dropTab;
        _dropTab = null;
        var zone = _zoneUnder;
        _zoneUnder = null;
        GetCursorPos(out var c);
        if (commit && tab?.Tab is { } to && drag.Drag is { } item)
        {
            // На другую вкладку: из категории — переносит, из «Все приложения» — добавляет.
            var from = drag.FromCategory;
            _s.Config.Update(cfg =>
            {
                var target = cfg.Categories.FirstOrDefault(x => x.Id == to);
                if (target is null) return;
                if (from != null) cfg.Categories.FirstOrDefault(x => x.Id == from)?.Items.Remove(item);
                if (target.Items.Any(i => string.Equals(i.Target, item.Target, StringComparison.OrdinalIgnoreCase))) return;
                var copy = from != null ? item : new LauncherItem { Kind = item.Kind, Title = item.Title, Target = item.Target, Meta = item.Meta };
                if (from is null) copy.Id = _s.Config.NewId(item.Title);
                target.Items.Add(copy);
            });
        }
        else if (commit && zone != null && drag.Drag is { } dropped) zone.Drop(c.X, c.Y, new DragPayload(dropped, drag.FromCategory));
        foreach (var z in _s.DropZones) z.DragLeave();
        Paint();
    }

    // ---------- общее ----------

    /// Иконка от Проводника, пока её нет — значок.
    FrameworkElement Icon(string? source, string glyph, double size)
    {
        var fallback = new FontIcon { Glyph = glyph, FontFamily = Theme.Icons, FontSize = size * 0.7, Foreground = Theme.Brush(Theme.TextSecondary) };
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        var box = new Grid { Width = size, Height = size, Children = { fallback, img }, VerticalAlignment = VerticalAlignment.Center };
        if (source != null)
        {
            _s.Icons.Get(source, (int)Math.Ceiling(size * Win.Scale), bmp =>
            {
                img.Source = bmp;
                fallback.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
            });
        }
        return box;
    }

    protected override void OnClosing()
    {
        if (_dragCell != null) FinishDrag(commit: false);
        _pressCell = null;
        _hoverCell = null;
        _filesCancel?.Cancel();
        _filesDelay.Stop();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
