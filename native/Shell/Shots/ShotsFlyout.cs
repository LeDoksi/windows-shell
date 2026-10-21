using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using static WindowsShell.Tail.FlyoutUi;

namespace WindowsShell.Shots;

/// Окошко значка «Снимки» (design-v2 п. 4): сверху «Снимок» и «Запись», ниже последние 5
/// плиткой, «Показать всё за 3 дня» раскрывает всё по дням. Плитку можно тянуть в окно; при
/// наведении — копировать, изменить, показать в папке, удалить; правый клик — всё это меню.
public sealed class ShotsFlyout : Popup
{
    const double TileW = 112, TileH = 70;
    const int Recent = 5;

    readonly ShotsService _shots;
    readonly MenuPopup _menu = new();
    readonly StackPanel _body = new() { Padding = new Thickness(14, 14, 14, 10), Spacing = 10 };
    readonly Dictionary<string, (DateTime Stamp, ImageSource? Image)> _images = new(StringComparer.OrdinalIgnoreCase);
    bool _all;
    TextBlock? _recLabel;

    public ShotsFlyout(ShotsService shots) : base(activating: true)
    {
        _shots = shots;
        SetContent(_body);
        _shots.Changed += () => { if (IsOpen) { Build(); Layout(); } };
        _shots.Tick += () => { if (IsOpen && _recLabel != null) _recLabel.Text = $"Стоп · {ShotsService.Length(_shots.RecordingTime)}"; };
    }

    protected override double Width => 392;
    protected override double Gap => 8;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(width, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    public void Show(object anchor, Rect anchorPx, object owner)
    {
        _all = false;
        _shots.Refresh();
        Build();
        Open(anchor, anchorPx, Placement.Above, owner: owner);
    }

    void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(Actions());
        var items = _shots.Items;
        if (items.Count == 0)
        {
            _body.Children.Add(new TextBlock
            {
                Text = "Здесь появятся снимки и записи за 3 дня",
                FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 12),
            });
        }
        else if (!_all)
        {
            _body.Children.Add(Header("Последние", items[0].Time.Date == DateTime.Today ? "Сегодня" : Day(items[0].Time), null));
            _body.Children.Add(Tiles(items.Take(Recent)));
            if (items.Count > Recent) _body.Children.Add(Link($"Показать всё за 3 дня ({items.Count})", () => { _all = true; Build(); Layout(); }));
        }
        else
        {
            _body.Children.Add(Header("Всё за 3 дня", "Свернуть", () => { _all = false; Build(); Layout(); }));
            var days = new StackPanel { Spacing = 8 };
            foreach (var day in items.GroupBy(i => i.Time.Date))
            {
                days.Children.Add(Label($"{Day(day.Key)} · {day.Count()}", 12, secondary: true));
                days.Children.Add(Tiles(day));
            }
            _body.Children.Add(new ScrollViewer { Content = days, MaxHeight = 440, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) });
        }
        _body.Children.Add(Link("Открыть папку", () => { Close(restoreFocus: false); ShotsService.OpenFolder(); }));
    }

    UIElement Actions()
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Окошко закрываем и ждём, пока оно исчезнет: иначе оно попадёт в снимок.
        g.Children.Add(Big("", "Снимок", "PrtSc", () => After(_shots.Snap), out _));
        var rec = _shots.Recording
            ? Big("", $"Стоп · {ShotsService.Length(_shots.RecordingTime)}", "Shift+PrtSc", () => { Close(restoreFocus: false); _shots.StopRecording(); }, out _recLabel, red: true)
            : Big("", "Запись", "Shift+PrtSc", () => After(_shots.Record), out _, red: true);
        if (!_shots.Recording) _recLabel = null;
        Grid.SetColumn(rec, 1);
        g.Children.Add(rec);
        return g;
    }

    void After(Action action)
    {
        Close(restoreFocus: true);
        Win.Window.DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(200);
            action();
        });
    }

    static Button Big(string glyph, string text, string key, Action click, out TextBlock label, bool red = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = Icon(glyph, 15);
        if (red) icon.Foreground = Theme.Brush(Windows.UI.Color.FromArgb(255, 0xFF, 0x5B, 0x5B));
        content.Children.Add(icon);
        label = Label(text, 13);
        content.Children.Add(label);
        content.Children.Add(Label(key, 11, secondary: true));
        var b = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(Theme.Card),
            BorderBrush = Theme.Brush(Theme.Stroke),
            BorderThickness = new Thickness(1),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static Grid Header(string left, string right, Action? click)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Label(left, 12, secondary: true));
        var r = Label(right, 12, secondary: click is null);
        if (click != null)
        {
            r.Foreground = Theme.Brush(Theme.Accent);
            r.PointerPressed += (_, e) => { e.Handled = true; click(); };
        }
        Grid.SetColumn(r, 1);
        g.Children.Add(r);
        return g;
    }

    static string Day(DateTime d)
    {
        if (d.Date == DateTime.Today) return "Сегодня";
        if (d.Date == DateTime.Today.AddDays(-1)) return "Вчера";
        return d.ToString("d MMMM");
    }

    Grid Tiles(IEnumerable<ShotItem> items)
    {
        var g = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var c = 0; c < 3; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileW) });
        var i = 0;
        foreach (var item in items)
        {
            if (i % 3 == 0) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileH) });
            var tile = Tile(item);
            Grid.SetRow(tile, i / 3);
            Grid.SetColumn(tile, i % 3);
            g.Children.Add(tile);
            i++;
        }
        return g;
    }

    FrameworkElement Tile(ShotItem item)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        LoadImage(item, image);
        var tile = new Grid
        {
            Width = TileW, Height = TileH,
            CornerRadius = new CornerRadius(6),
            BorderBrush = Theme.Brush(Theme.Stroke), BorderThickness = new Thickness(1),
            Background = Theme.Brush(Windows.UI.Color.FromArgb(255, 0x20, 0x22, 0x26)),
            Children = { image },
        };
        if (!item.IsImage)
        {
            tile.Children.Add(new Border
            {
                Background = Theme.Brush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 2),
                Margin = new Thickness(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = item.Kind == ShotKind.Gif ? "GIF" : "▶ Видео", FontFamily = Theme.Font, FontSize = 10, Foreground = Theme.Brush(Theme.Text) },
            });
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        actions.Children.Add(Mini("", "Копировать", () => _shots.Copy(item)));
        if (item.IsImage) actions.Children.Add(Mini("", "Изменить", () => { Close(restoreFocus: false); _shots.Edit(item); }));
        actions.Children.Add(Mini("", "Показать в папке", () => { Close(restoreFocus: false); _shots.Reveal(item); }));
        actions.Children.Add(Mini("", "Удалить", () => _shots.Delete(item)));
        tile.Children.Add(actions);
        ToolTipService.SetToolTip(tile, $"{item.Name}\n{item.Time:dd.MM HH:mm}");

        tile.PointerEntered += (_, _) => { actions.Visibility = Visibility.Visible; tile.BorderBrush = Theme.Brush(Theme.Accent); };
        tile.PointerExited += (_, _) => { actions.Visibility = Visibility.Collapsed; tile.BorderBrush = Theme.Brush(Theme.Stroke); };

        var pressed = false;
        var at = new Point();
        tile.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(tile);
            if (p.Properties.IsRightButtonPressed) { e.Handled = true; TileMenu(item, tile); return; }
            if (!p.Properties.IsLeftButtonPressed) return;
            pressed = true;
            at = p.Position;
            tile.CapturePointer(e.Pointer);
        };
        tile.PointerMoved += (_, e) =>
        {
            if (!pressed) return;
            var p = e.GetCurrentPoint(tile).Position;
            if (Math.Abs(p.X - at.X) + Math.Abs(p.Y - at.Y) < 6) return;
            pressed = false;
            tile.ReleasePointerCaptures();
            if (FileDrag.Start(Hwnd, item.Path)) Close(restoreFocus: false);
        };
        tile.PointerReleased += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            tile.ReleasePointerCaptures();
            if (e.GetCurrentPoint(tile).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;
            Close(restoreFocus: false);
            _shots.Edit(item);
        };
        return tile;
    }

    static Button Mini(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontFamily = Theme.Icons, FontSize = 11 },
            Width = 22, Height = 22, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = Theme.Brush(Windows.UI.Color.FromArgb(0xD9, 0x14, 0x16, 0x19)),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(b, tip);
        // Нажатие на кнопку — не нажатие на плитку (не открывать редактор, не тащить).
        b.PointerPressed += (_, e) => e.Handled = true;
        b.Click += (_, _) => click();
        return b;
    }

    void TileMenu(ShotItem item, FrameworkElement tile)
    {
        var items = new List<MenuItem>
        {
            new() { Text = item.IsImage ? "Изменить" : "Открыть", Glyph = item.IsImage ? "" : "", Invoke = () => { Close(restoreFocus: false); _shots.Edit(item); } },
            new() { Text = "Копировать", Glyph = "", Invoke = () => _shots.Copy(item) },
            new() { Text = "Сохранить как…", Glyph = "", Invoke = () => { var h = Hwnd; Close(restoreFocus: false); _shots.SaveAs(item, h); } },
        };
        if (item.Kind == ShotKind.Video)
            items.Add(new() { Text = "Сохранить как GIF", Glyph = "", Invoke = () => { Close(restoreFocus: false); _shots.SaveGif(item); } });
        items.Add(new() { Text = "Показать в папке", Glyph = "", Invoke = () => { Close(restoreFocus: false); _shots.Reveal(item); } });
        items.Add(MenuItem.Sep);
        items.Add(new() { Text = "Удалить", Glyph = "", Danger = true, Invoke = () => _shots.Delete(item) });
        var t = tile.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var scale = Win.Scale;
        GetWindowRect(Hwnd, out var r);
        var px = new Rect(r.Left + t.X * scale, r.Top + t.Y * scale, tile.ActualWidth * scale, tile.ActualHeight * scale);
        _menu.ShowInside(this, ("shot", item.Path), px, items);
    }

    async void LoadImage(ShotItem item, Image image)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(item.Path); }
        catch { return; }
        if (_images.TryGetValue(item.Path, out var cached) && cached.Stamp == stamp) { image.Source = cached.Image; return; }
        var scale = Win.Scale;
        var source = await ShotImages.Load(item, (int)(TileW * 2 * scale));
        _images[item.Path] = (stamp, source);
        image.Source = source;
    }
}

/// Крупное превью последнего снимка при наведении на значок «Снимки» (design-v2 п. 4).
public sealed class ShotPeek
{
    const double W = 320;
    readonly ShellWindow _win = new(Surface.Menu, noActivate: true, rounded: true);
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _caption = new() { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextSecondary), Margin = new Thickness(10, 6, 10, 7) };
    int _version;

    public ShotPeek()
    {
        _win.Root.IsHitTestVisible = false;
        _win.Root.Children.Add(new StackPanel { Children = { _image, _caption } });
    }

    public async void Show(ShotItem item, Rect anchorPx)
    {
        var v = ++_version;
        var mon = MonitorFromPoint(new POINT { X = (int)(anchorPx.X + anchorPx.Width / 2), Y = (int)anchorPx.Y }, MONITOR_DEFAULTTONEAREST);
        var scale = MonitorScale(mon);
        var source = await ShotImages.Load(item, (int)(W * scale));
        if (v != _version || source is null) return;
        _image.Source = source;
        var ih = source is Microsoft.UI.Xaml.Media.Imaging.BitmapSource { PixelWidth: > 0 } b ? Math.Clamp(W * b.PixelHeight / b.PixelWidth, 80, 240) : 180;
        _image.Width = W;
        _image.Height = ih;
        _caption.Text = $"{(item.IsImage ? "Снимок" : item.Kind == ShotKind.Gif ? "GIF" : "Запись")} · {item.Time:HH:mm}";
        var area = MonitorInfo(mon).rcMonitor;
        var w = (int)Math.Ceiling(W * scale);
        var h = (int)Math.Ceiling((ih + 32) * scale);
        var x = Math.Clamp((int)(anchorPx.X + anchorPx.Width / 2 - w / 2.0), area.Left + 8, area.Right - 8 - w);
        var y = Math.Max(area.Top + 8, (int)(anchorPx.Y - 10 * scale - h));
        _win.Place(x, y, w, h);
        _win.Show(activate: false);
        _win.Raise();
    }

    public void Hide()
    {
        _version++;
        if (_win.Visible) _win.Hide();
    }
}
