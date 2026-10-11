using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Config;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Panel;

/// Окно категории под её пилюлей (раздел 4.1): плитки программ, файлов, папок и ссылок.
/// Печать фильтрует, стрелки и Enter работают, плитки перетаскиваются: внутри — порядок,
/// на другую пилюлю — в другую категорию, на док — закрепить. Файлы из Проводника
/// добавляются туда, куда их бросили.
public sealed class CategoryWindow : Popup
{
    const double TileW = 96, TileH = 92, Columns = 6, Spacing = 4, PadX = 14;

    readonly Services _s;
    readonly Ellipse _dot = new() { Width = 10, Height = 10 };
    readonly TextBlock _name = new() { FontFamily = Theme.Font, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBox _filter = new() { PlaceholderText = "Найти в категории", FontFamily = Theme.Font, FontSize = 13, Width = 200, Height = 32 };
    readonly Grid _tiles = new() { ColumnSpacing = Spacing, RowSpacing = Spacing };
    readonly ScrollViewer _scroll;
    readonly TextBlock _empty = new()
    {
        Text = "Перетащи сюда программы, файлы или папки",
        FontFamily = Theme.Font,
        FontSize = 13,
        Foreground = Theme.Brush(Theme.TextSecondary),
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 28, 0, 28),
    };
    readonly DragGhost _ghost = new();
    readonly List<(LauncherItem Item, Border Tile)> _shown = [];
    string _categoryId = "";
    int _selected = -1;

    // Перетаскивание плитки.
    LauncherItem? _pressItem;
    Microsoft.UI.Xaml.Input.Pointer? _pointer;
    // Во время перетаскивания мышь захвачена окном, а не плиткой: плитки пересобираются.
    bool _rootCapture;
    // За пределами окна события мыши не приходят: пока тащат плитку, опрашиваем курсор сами.
    readonly DispatcherTimer _dragPoll = new() { Interval = TimeSpan.FromMilliseconds(16) };
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    POINT _pressAt;
    LauncherItem? _dragItem;
    int _dragIndex = -1;
    IDropZone? _zone;
    ImageSource? _dragIcon;

    // Файлы из Проводника.
    int _dropIndex = -1;

    public CategoryWindow(Services services) : base(activating: true)
    {
        _s = services;
        _dragPoll.Tick += (_, _) =>
        {
            if (_dragItem is null) { _dragPoll.Stop(); return; }
            GetCursorPos(out var c);
            if (GetAsyncKeyState(0x1B) < 0) { FinishDrag(commit: false); return; }
            if (GetAsyncKeyState(0x01) >= 0) { FinishDrag(commit: true); return; }
            UpdateDrag(c);
        };
        Win.Root.PointerMoved += (_, e) => { if (_rootCapture) OnTileMoved(e); };
        Win.Root.PointerReleased += (_, e) =>
        {
            if (!_rootCapture) return;
            e.Handled = true;
            FinishDrag(commit: true);
        };
        Win.Root.PointerCaptureLost += (_, _) =>
        {
            if (!_rootCapture) return;
            _rootCapture = false;
            if (_dragItem != null) EndDrag(commit: false);
            _pressItem = null;
        };
        for (var i = 0; i < Columns; i++) _tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileW) });
        var edit = new Button
        {
            Content = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 14 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(edit, "Изменить");
        edit.Click += (_, _) => ShowEditMenu(edit);
        var head = new Grid { ColumnSpacing = 10, Margin = new Thickness(2, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _dot.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_dot);
        Grid.SetColumn(_name, 1);
        head.Children.Add(_name);
        Grid.SetColumn(_filter, 2);
        head.Children.Add(_filter);
        Grid.SetColumn(edit, 3);
        head.Children.Add(edit);
        _scroll = new ScrollViewer { Content = _tiles, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = new StackPanel { Padding = new Thickness(PadX, 12, PadX, 14), Children = { head, _scroll, _empty } };
        body.AllowDrop = true;
        body.DragOver += OnFileDragOver;
        body.DragLeave += (_, _) =>
        {
            // DragLeave приходит и при переходе между плитками: сбрасываем, только если курсор ушёл из окна.
            if (GetCursorPos(out var c) && InsideWindow(c)) return;
            _dropIndex = -1;
            Build();
        };
        body.Drop += OnFileDrop;
        body.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
        SetContent(body);

        _filter.TextChanged += (_, _) => { _selected = 0; Build(); };
        _filter.PreviewKeyDown += OnFilterKey;
        _s.Config.Changed += () => { if (IsOpen && _dragItem is null) { if (Category is null) Close(restoreFocus: false); else Build(); } };
    }

    protected override double Width => PadX * 2 + Columns * TileW + (Columns - 1) * Spacing;
    protected override double Gap => 8;

    Category? Category => _s.Config.CategoryById(_categoryId);

    /// Какая категория открыта (null — окно закрыто).
    public string? OpenCategory => IsOpen ? _categoryId : null;

    public void Show(string categoryId, Rect anchorPx, object owner)
    {
        _categoryId = categoryId;
        _filter.Text = "";
        _selected = 0;
        Build();
        Open((owner, categoryId), anchorPx, Placement.Below, owner: owner);
        _filter.Focus(FocusState.Programmatic);
    }

    List<LauncherItem> Visible()
    {
        var items = Category?.Items ?? [];
        var q = _filter.Text.Trim();
        if (q.Length == 0) return [.. items];
        return items.Where(i => i.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    void Build()
    {
        var cat = Category;
        if (cat is null) return;
        _name.Text = cat.Name;
        _dot.Fill = Theme.Brush(Theme.Parse(cat.Color));
        var items = Visible();
        // Пока тащат плитку, она стоит там, куда встанет.
        if (_dragItem != null && _dragIndex >= 0)
        {
            items.Remove(_dragItem);
            items.Insert(Math.Clamp(_dragIndex, 0, items.Count), _dragItem);
        }
        _tiles.Children.Clear();
        _tiles.RowDefinitions.Clear();
        _shown.Clear();
        var count = items.Count + (_dropIndex >= 0 ? 1 : 0);
        var rows = (int)Math.Ceiling(count / Columns);
        for (var r = 0; r < rows; r++) _tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileH) });
        var slot = 0;
        for (var i = 0; i < items.Count; i++)
        {
            // Место, куда лягут файлы из Проводника.
            if (i == _dropIndex) slot++;
            var tile = MakeTile(items[i], i == _selected && _dragItem is null);
            if (items[i] == _dragItem) tile.Opacity = 0.35;
            Grid.SetColumn(tile, slot % (int)Columns);
            Grid.SetRow(tile, slot / (int)Columns);
            _tiles.Children.Add(tile);
            _shown.Add((items[i], tile));
            slot++;
        }
        _empty.Visibility = items.Count == 0 && _dropIndex < 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0 && cat.Items.Count > 0) _empty.Text = "Ничего не найдено";
        else _empty.Text = "Перетащи сюда программы, файлы или папки";
        if (IsOpen) Layout();
    }

    Border MakeTile(LauncherItem item, bool selected)
    {
        var icon = new Image { Width = 40, Height = 40, Stretch = Stretch.Uniform };
        var glyph = new FontIcon
        {
            Glyph = item.Kind switch { "url" => "", "folder" => "", "file" => "", _ => "" },
            FontFamily = Theme.Icons,
            FontSize = 28,
            Foreground = Theme.Brush(Theme.TextSecondary),
        };
        var iconBox = new Grid { Width = 40, Height = 40, Children = { glyph, icon } };
        if (item.Kind != "url")
        {
            var source = item.Kind == "app" ? Launcher.ShellName(item.Target) : item.Target;
            _s.Icons.Get(source, (int)Math.Ceiling(40 * Win.Scale), bmp =>
            {
                icon.Source = bmp;
                glyph.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
            });
        }
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
        var tile = new Border
        {
            Width = TileW,
            Height = TileH,
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(selected ? Theme.Selected : Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(0, 10, 0, 0),
            Child = new StackPanel { Children = { iconBox, title } },
            Opacity = Exists(item) ? 1 : 0.4,
        };
        ToolTipService.SetToolTip(tile, Exists(item) ? item.Title : $"{item.Title} — не найдено");
        tile.PointerEntered += (_, _) => { if (_dragItem is null) tile.Background = Theme.Brush(Theme.Hover); };
        tile.PointerExited += (_, _) => { if (_dragItem is null) tile.Background = Theme.Brush(_shown.FindIndex(x => x.Tile == tile) == _selected ? Theme.Selected : Microsoft.UI.Colors.Transparent); };
        tile.PointerPressed += (_, e) => OnTilePressed(tile, item, e);
        tile.PointerMoved += (_, e) => OnTileMoved(e);
        tile.PointerReleased += (_, e) => OnTileReleased(tile, item, e);
        tile.PointerCaptureLost += (_, _) => { if (_rootCapture) return; if (_dragItem != null) EndDrag(commit: false); _pressItem = null; };
        return tile;
    }

    static bool Exists(LauncherItem item) => item.Kind switch
    {
        "url" => true,
        "folder" => Directory.Exists(item.Target),
        "file" => File.Exists(item.Target),
        _ => Launcher.Resolve(item.Target).Exists,
    };

    void OpenItem(LauncherItem item)
    {
        Close(restoreFocus: false);
        if (item.Kind == "app") Launcher.LaunchInBackground(item.Target);
        else Launcher.Open(item.Target);
    }

    // ---------- клавиатура ----------

    void OnFilterKey(object sender, KeyRoutedEventArgs e)
    {
        var n = _shown.Count;
        switch (e.Key)
        {
            case VirtualKey.Escape when _dragItem != null:
                EndDrag(commit: false);
                break;
            case VirtualKey.Escape when _filter.Text.Length > 0:
                _filter.Text = "";
                break;
            case VirtualKey.Enter when n > 0:
                OpenItem(_shown[Math.Clamp(_selected, 0, n - 1)].Item);
                break;
            case VirtualKey.Right when n > 0 && _filter.SelectionStart >= _filter.Text.Length:
                Select(Math.Min(n - 1, _selected + 1));
                break;
            case VirtualKey.Left when n > 0 && _filter.SelectionStart == 0 && _filter.SelectionLength == 0:
                Select(Math.Max(0, _selected - 1));
                break;
            case VirtualKey.Down when n > 0:
                Select(Math.Min(n - 1, _selected + (int)Columns));
                break;
            case VirtualKey.Up when n > 0:
                Select(Math.Max(0, _selected - (int)Columns));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void Select(int index)
    {
        _selected = index;
        for (var i = 0; i < _shown.Count; i++)
            _shown[i].Tile.Background = Theme.Brush(i == index ? Theme.Selected : Microsoft.UI.Colors.Transparent);
        if (index >= 0 && index < _shown.Count) _shown[index].Tile.StartBringIntoView();
    }

    // ---------- мышь и перетаскивание ----------

    void OnTilePressed(Border tile, LauncherItem item, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(tile);
        e.Handled = true;
        if (!p.Properties.IsLeftButtonPressed) return;
        _pressItem = item;
        _pointer = e.Pointer;
        GetCursorPos(out _pressAt);
        tile.CapturePointer(e.Pointer);
    }

    void OnTileMoved(PointerRoutedEventArgs e)
    {
        if (_pressItem is null) return;
        GetCursorPos(out var c);
        if (_dragItem is null)
        {
            var threshold = 6 * Win.Scale;
            if (Math.Abs(c.X - _pressAt.X) + Math.Abs(c.Y - _pressAt.Y) < threshold) return;
            BeginDrag(_pressItem, c);
        }
        UpdateDrag(c);
    }

    void OnTileReleased(Border tile, LauncherItem item, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var kind = e.GetCurrentPoint(tile).Properties.PointerUpdateKind;
        // Захват отпускаем последним: PointerCaptureLost сбрасывает нажатие и отменяет перетаскивание.
        if (_dragItem != null) { EndDrag(commit: true); _pressItem = null; tile.ReleasePointerCaptures(); return; }
        var pressed = _pressItem;
        _pressItem = null;
        tile.ReleasePointerCaptures();
        if (kind == PointerUpdateKind.LeftButtonReleased && pressed == item) OpenItem(item);
        else if (kind == PointerUpdateKind.RightButtonReleased) ShowTileMenu(item);
    }

    /// Тащат плитку из окна (панель тогда не переключает категории наведением).
    public bool Dragging => _dragItem != null;

    void FinishDrag(bool commit)
    {
        _dragPoll.Stop();
        EndDrag(commit);
        _pressItem = null;
        _rootCapture = false;
        Win.Root.ReleasePointerCaptures();
    }

    void BeginDrag(LauncherItem item, POINT c)
    {
        _dragPoll.Start();
        _rootCapture = true;
        if (_pointer != null) Win.Root.CapturePointer(_pointer);
        _dragItem = item;
        _dragIndex = Visible().IndexOf(item);
        _dragIcon = _shown.FirstOrDefault(x => x.Item == item).Tile is { } t ? FindImage(t) : null;
        _ghost.Show(_dragIcon, c.X, c.Y);
        Build();
    }

    static ImageSource? FindImage(DependencyObject root)
    {
        if (root is Image { Source: { } src }) return src;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++) if (FindImage(VisualTreeHelper.GetChild(root, i)) is { } s) return s;
        return null;
    }

    bool InsideWindow(POINT c) => GetWindowRect(Hwnd, out var r) && c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom;

    /// Индекс плитки под точкой экрана (для вставки).
    int IndexAt(POINT c)
    {
        GetWindowRect(Hwnd, out var r);
        var scale = Win.Scale;
        var origin = _tiles.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var x = (c.X - r.Left) / scale - origin.X;
        var y = (c.Y - r.Top) / scale - origin.Y;
        var col = Math.Clamp((int)Math.Floor((x + Spacing / 2) / (TileW + Spacing)), 0, (int)Columns - 1);
        var row = Math.Max(0, (int)Math.Floor((y + Spacing / 2) / (TileH + Spacing)));
        // Правая половина плитки — после неё.
        var within = x - col * (TileW + Spacing);
        var index = row * (int)Columns + col + (within > TileW / 2 ? 1 : 0);
        return index;
    }

    void UpdateDrag(POINT c)
    {
        _ghost.MoveTo(c.X, c.Y);
        var payload = new DragPayload(_dragItem!, _categoryId);
        if (InsideWindow(c))
        {
            _zone?.DragLeave();
            _zone = null;
            if (_filter.Text.Length > 0) return;
            var count = Category?.Items.Count ?? 0;
            var index = Math.Clamp(IndexAt(c), 0, Math.Max(0, count - 1));
            if (index != _dragIndex) { _dragIndex = index; Build(); }
            return;
        }
        IDropZone? hit = null;
        foreach (var z in _s.DropZones)
        {
            if (hit is null && z.DragOver(c.X, c.Y, payload)) hit = z;
            else z.DragLeave();
        }
        _zone = hit;
    }

    void EndDrag(bool commit)
    {
        var item = _dragItem;
        _dragItem = null;
        _ghost.Hide();
        if (item is null) return;
        GetCursorPos(out var c);
        var index = _dragIndex;
        _dragIndex = -1;
        var zone = _zone;
        _zone = null;
        if (commit && InsideWindow(c) && _filter.Text.Length == 0)
        {
            _s.Config.Update(cfg =>
            {
                var cat = cfg.Categories.FirstOrDefault(x => x.Id == _categoryId);
                if (cat is null || !cat.Items.Remove(item)) return;
                cat.Items.Insert(Math.Clamp(index, 0, cat.Items.Count), item);
            });
        }
        else if (commit && zone != null)
        {
            zone.Drop(c.X, c.Y, new DragPayload(item, _categoryId));
        }
        else zone?.DragLeave();
        foreach (var z in _s.DropZones) z.DragLeave();
        Build();
    }

    // ---------- меню ----------

    void ShowTileMenu(LauncherItem item)
    {
        GetCursorPos(out var c);
        var items = new List<MenuItem>
        {
            new() { Text = "Открыть", Glyph = "", Invoke = () => OpenItem(item) },
        };
        if (item.Kind != "url")
        {
            var path = item.Kind == "app" && !Launcher.IsPath(item.Target) ? Launcher.Resolve(item.Target).Exe : item.Target;
            items.Add(new() { Text = "Открыть расположение", Glyph = "", Enabled = path != null && (File.Exists(path) || Directory.Exists(path)), Invoke = () => { if (path != null) Launcher.Reveal(path); } });
        }
        if (item.Kind == "app")
        {
            var pinned = _s.Config.Pins.Any(p => string.Equals(p.Target, item.Target, StringComparison.OrdinalIgnoreCase));
            items.Add(new() { Text = pinned ? "Уже в доке" : "Закрепить в доке", Glyph = "", Enabled = !pinned, Invoke = () => _s.PinApp(item.Title, item.Target) });
        }
        items.Add(MenuItem.Sep);
        items.Add(new() { Text = "Убрать из категории", Glyph = "", Danger = true, Invoke = () => _s.Config.Update(cfg => cfg.Categories.FirstOrDefault(x => x.Id == _categoryId)?.Items.Remove(item)) });
        _s.Menu.ShowInside(this, (this, item.Id), new Rect(c.X, c.Y, 0, 0), items);
    }

    void ShowEditMenu(FrameworkElement button)
    {
        var cat = Category;
        if (cat is null) return;
        var origin = button.TransformToVisual(null).TransformPoint(new Point(0, button.ActualHeight));
        GetWindowRect(Hwnd, out var r);
        var at = new Rect(r.Left + origin.X * Win.Scale, r.Top + origin.Y * Win.Scale, 0, 0);
        _s.Menu.ShowInside(this, (this, "edit"), at, CategoryMenu(_s, cat, () => _s.RenameCategory?.Invoke(cat.Id)));
    }

    /// Меню категории: то же у правого клика по пилюле и у карандаша в окне категории.
    public static List<MenuItem> CategoryMenu(Services s, Category cat, Action rename) =>
    [
        new() { Text = "Переименовать", Glyph = "", Invoke = rename },
        new()
        {
            Text = "Цвет",
            Glyph = "",
            Children = ShellConfig.Colors.Select(color => new MenuItem
            {
                Text = ColorName(color),
                Checked = string.Equals(cat.Color, color, StringComparison.OrdinalIgnoreCase),
                Invoke = () => s.Config.Update(_ => cat.Color = color),
            }).ToList(),
        },
        MenuItem.Sep,
        new()
        {
            Text = "Удалить категорию",
            Glyph = "",
            Danger = true,
            Children =
            [
                new() { Text = cat.Items.Count > 0 ? $"Удалить «{cat.Name}» и {cat.Items.Count} плиток" : $"Удалить «{cat.Name}»", Danger = true, Invoke = () => s.Config.Update(c => c.Categories.Remove(cat)) },
            ],
        },
    ];

    static string ColorName(string hex) => hex.ToUpperInvariant() switch
    {
        "#43B4F5" => "Голубой",
        "#B07CF0" => "Фиолетовый",
        "#56D27A" => "Зелёный",
        "#F0A64A" => "Оранжевый",
        "#F06B8E" => "Розовый",
        "#4CC3D9" => "Бирюзовый",
        "#F2C94C" => "Жёлтый",
        "#8E9AAF" => "Серый",
        _ => hex,
    };

    // ---------- файлы из Проводника ----------

    void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.None; return; }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"Добавить в «{Category?.Name}»";
        GetCursorPos(out var c);
        var index = _filter.Text.Length > 0 ? _shown.Count : Math.Clamp(IndexAt(c), 0, _shown.Count);
        if (index != _dropIndex) { _dropIndex = index; Build(); }
    }

    async void OnFileDrop(object sender, DragEventArgs e)
    {
        var index = _dropIndex;
        _dropIndex = -1;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) { Build(); return; }
        var deferral = e.GetDeferral();
        try
        {
            var files = await e.DataView.GetStorageItemsAsync();
            var items = files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p))
                .Select(p => ShellConfig.ItemFromPath(ShellConfig.KeepShortcut(p))).ToList();
            if (items.Count > 0) _s.AddToCategory(_categoryId, items, index < 0 ? null : index);
        }
        catch (Exception ex) { Log.Error("файлы в категорию", ex); }
        finally { deferral.Complete(); Build(); }
    }

    protected override void OnClosing()
    {
        if (_dragItem != null) EndDrag(commit: false);
        _dropIndex = -1;
    }
}
