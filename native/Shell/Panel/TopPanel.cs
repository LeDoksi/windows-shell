using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using WindowsShell.Config;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Panel;

/// Верхняя панель (раздел 4) на одном мониторе: пилюли категорий, виджеты («Сейчас играет»),
/// ＋ и ⚙. Выезжает у верхнего края любого монитора, окна категорий открываются под пилюлями.
public sealed class TopPanel : IBar, IDropZone
{
    const double H = 44, TopGap = 8;

    readonly Services _s;
    readonly ShellWindow _win;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _pills = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    readonly Rectangle _sep = new() { Width = 1, Height = 20, Fill = Theme.Brush(Theme.Stroke), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Border _media;
    readonly Image _mediaCover = new() { Width = 22, Height = 22, Stretch = Stretch.UniformToFill };
    readonly FontIcon _mediaNote = new() { Glyph = "", FontFamily = Theme.Icons, FontSize = 14, Foreground = Theme.Brush(Theme.TextSecondary) };
    readonly TextBlock _mediaTitle = new() { FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _mediaPlay;
    readonly FontIcon _mediaPlayIcon = new() { Glyph = "", FontFamily = Theme.Icons, FontSize = 13, Foreground = Theme.Brush(Theme.Text) };
    readonly Border _add;
    readonly Border _gear;
    readonly List<(Category Cat, Border Pill)> _pillList = [];
    readonly DispatcherQueueTimer _springTimer;

    Screen _screen;
    double _scale = 1;
    int _x, _y, _w, _h;
    int _slide;
    double _reveal;
    bool _shown;

    // Перетаскивание пилюли вдоль панели.
    Border? _pressPill;
    POINT _pressAt;
    Category? _dragCat;

    // Файлы или плитка над пилюлей.
    string? _springCat;
    Border? _dropPill;

    public TopPanel(Services services, Screen screen)
    {
        _s = services;
        _screen = screen;
        _win = new ShellWindow(Surface.Dock, noActivate: true, rounded: true, transitions: false);
        _win.Root.Children.Add(_row);

        var mediaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        mediaRow.Children.Add(new Grid { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Background = Theme.Brush(Theme.Card), Children = { _mediaNote, _mediaCover } });
        mediaRow.Children.Add(_mediaTitle);
        _media = new Border { Height = 32, Padding = new Thickness(6, 0, 8, 0), CornerRadius = new CornerRadius(6), Child = mediaRow, Background = Theme.Brush(Microsoft.UI.Colors.Transparent) };
        Hover(_media);
        _media.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            var kind = e.GetCurrentPoint(_media).Properties.PointerUpdateKind;
            // Средний клик — пауза/продолжить.
            if (kind == PointerUpdateKind.MiddleButtonReleased) { _s.Media.PlayPause(); return; }
            if (kind != PointerUpdateKind.LeftButtonReleased) return;
            Popups.Toggle((this, "media"), () => _s.MediaFlyout.Show((this, "media"), AnchorOf(_media), this));
        };
        _mediaPlay = IconButton(_mediaPlayIcon, "Пауза / играть", () => _s.Media.PlayPause());
        _add = IconButton(new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 14, Foreground = Theme.Brush(Theme.Text) }, "Новая категория", NewCategory);
        _gear = IconButton(new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 14, Foreground = Theme.Brush(Theme.Text) }, "Настройки виджетов", () => _s.ToggleWidgetSettings(AnchorOf(_gear!), this));

        _springTimer = _s.Queue.CreateTimer();
        _springTimer.Interval = TimeSpan.FromMilliseconds(600);
        _springTimer.IsRepeating = false;
        // Файл держат над категорией 600 мс — она открывается, файл можно бросить внутрь.
        _springTimer.Tick += (_, _) =>
        {
            var pill = _pillList.FirstOrDefault(p => p.Cat.Id == _springCat);
            if (pill.Pill != null && _s.CategoryWindow.OpenCategory != pill.Cat.Id) OpenCategory(pill.Cat, pill.Pill);
        };

        _win.Window.Activated += (_, e) =>
        {
            // Клик по панели не забирает фокус у окна, в котором работаешь (0.6).
            if (e.WindowActivationState != WindowActivationState.Deactivated && GetForegroundWindow() == _win.Hwnd) _s.Tracker.RestoreForeground();
        };
        _s.Config.Changed += Build;
        _s.Media.Changed += UpdateMedia;
        _s.DropZones.Add(this);
    }

    // ---------- IBar ----------

    public Screen Screen => _screen;
    public bool AtTop => true;
    public bool Pinned => _s.Config.Bars.LauncherPinned;
    public (int Left, int Right) EdgeSpan => (_x, _x + _w);
    public RECT HotRect => new() { Left = _x - Px(8), Top = _screen.Bounds.Top, Right = _x + _w + Px(8), Bottom = _y + _h + Px(6) };
    public int Reserve => Px(H + TopGap + 6);
    public bool Busy => _dragCat != null || _dropPill != null || Popups.OwnedBy(this);
    public IntPtr MainHwnd => _win.Hwnd;

    public void SetReveal(double progress)
    {
        _reveal = progress;
        _slide = (int)Math.Round((1 - progress) * (_h + Px(TopGap) + 4));
        if (progress <= 0)
        {
            if (_shown) { _win.Hide(); _shown = false; }
            return;
        }
        Place();
        if (!_shown)
        {
            _win.Show(activate: false);
            _shown = true;
        }
        _win.Raise();
        if (Popups.Current is { IsOpen: true } p) SetWindowPos(p.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    int Px(double dip) => (int)Math.Round(dip * _scale);

    // ---------- место ----------

    public void Show()
    {
        _scale = _screen.Scale;
        Build();
        _s.Bars.Add(this);
    }

    public void Move(Screen screen)
    {
        if (screen.SameAs(_screen)) return;
        _screen = screen;
        _scale = screen.Scale;
        Measure();
        Place();
        _s.Bars.Publish();
    }

    void Measure()
    {
        _row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wDip = Math.Min(_row.DesiredSize.Width, _screen.Bounds.Width / _scale - 32);
        _w = Px(wDip);
        _h = Px(H);
        _x = _screen.Bounds.Left + (_screen.Bounds.Width - _w) / 2;
        _y = _screen.Bounds.Top + Px(TopGap);
    }

    void Place()
    {
        if (_reveal <= 0) return;
        _win.Place(_x, _y - _slide, _w, _h);
    }

    public void Close()
    {
        _s.Bars.Remove(this);
        _s.DropZones.Remove(this);
        _s.Config.Changed -= Build;
        _s.Media.Changed -= UpdateMedia;
        if (Popups.OwnedBy(this)) Popups.CloseAll();
        _win.Window.Close();
    }

    /// Прямоугольник элемента на экране (физические пиксели).
    Rect AnchorOf(FrameworkElement el)
    {
        var o = el.TransformToVisual(null).TransformPoint(new Point(0, 0));
        GetWindowRect(_win.Hwnd, out var r);
        return new Rect(r.Left + o.X * _scale, r.Top + o.Y * _scale, el.ActualWidth * _scale, el.ActualHeight * _scale);
    }

    // ---------- содержимое ----------

    Border IconButton(UIElement icon, string tip, Action click)
    {
        var b = new Border
        {
            Width = 34,
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            Child = icon,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (icon is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }
        ToolTipService.SetToolTip(b, tip);
        Hover(b);
        b.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            if (e.GetCurrentPoint(b).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased) click();
        };
        return b;
    }

    static void Hover(Border b)
    {
        b.PointerEntered += (_, _) => b.Background = Theme.Brush(Theme.Hover);
        b.PointerExited += (_, _) => b.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
    }

    string _built = "";

    void Build()
    {
        if (_dragCat != null) return;
        // Панель пересобирается, только если поменялись категории или виджеты (WSH-54).
        var media = _s.Config.WidgetEnabled("media") && _s.Media.Current != null;
        var key = string.Join("|", _s.Config.Categories.Select(c => $"{c.Id}:{c.Name}:{c.Color}")) + $"|{media}";
        if (key == _built && _row.Children.Count > 0) { Highlight(); return; }
        _built = key;
        _row.Children.Clear();
        _pills.Children.Clear();
        _pillList.Clear();
        var open = _s.CategoryWindow.OpenCategory;
        foreach (var cat in _s.Config.Categories)
        {
            var pill = MakePill(cat, cat.Id == open && ReferenceEquals(_s.CategoryWindow.Owner, this));
            _pills.Children.Add(pill);
            _pillList.Add((cat, pill));
        }
        _row.Children.Add(_pills);
        _row.Children.Add(_sep);
        if (media)
        {
            _row.Children.Add(_media);
            _row.Children.Add(_mediaPlay);
        }
        _row.Children.Add(_add);
        _row.Children.Add(_gear);
        UpdateMedia();
        Measure();
        Place();
        _s.Bars.Publish();
    }

    Border MakePill(Category cat, bool selected)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = Theme.Brush(Theme.Parse(cat.Color)), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = cat.Name, FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center });
        var pill = new Border
        {
            Height = 32,
            Padding = new Thickness(10, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(selected ? Theme.Selected : Microsoft.UI.Colors.Transparent),
            Child = content,
            AllowDrop = true,
            Tag = cat.Id,
        };
        pill.PointerEntered += (_, _) =>
        {
            if (_dragCat != null) return;
            pill.Background = Theme.Brush(Theme.Hover);
            // Другая категория уже открыта: наведение переключает, как строка меню Windows (раздел 4).
            if (_s.CategoryWindow.OpenCategory is { } openId && openId != cat.Id && ReferenceEquals(_s.CategoryWindow.Owner, this))
                OpenCategory(cat, pill);
        };
        pill.PointerExited += (_, _) => { if (_dragCat is null) Highlight(); };
        pill.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            if (!e.GetCurrentPoint(pill).Properties.IsLeftButtonPressed) return;
            _pressPill = pill;
            GetCursorPos(out _pressAt);
            pill.CapturePointer(e.Pointer);
        };
        pill.PointerMoved += (_, _) => OnPillMoved(cat);
        pill.PointerReleased += (_, e) => OnPillReleased(pill, cat, e);
        pill.PointerCaptureLost += (_, _) => { if (_dragCat != null) EndPillDrag(commit: false); _pressPill = null; };
        pill.DragEnter += (_, e) => OnFileDragOver(cat, pill, e);
        pill.DragOver += (_, e) => OnFileDragOver(cat, pill, e);
        pill.DragLeave += (_, _) =>
        {
            // Переход между точкой и текстом пилюли — не уход с неё.
            if (GetCursorPos(out var c) && AnchorOf(pill) is var a && c.X >= a.X && c.X < a.X + a.Width && c.Y >= a.Y && c.Y < a.Y + a.Height) return;
            _springTimer.Stop();
            _springCat = null;
            _dropPill = null;
            Highlight();
        };
        pill.Drop += (_, e) => OnFileDrop(cat, e);
        return pill;
    }

    /// Подсветка: открытая категория и та, над которой держат файл или плитку.
    void Highlight()
    {
        var open = _s.CategoryWindow.OpenCategory;
        foreach (var (cat, pill) in _pillList)
        {
            var on = pill == _dropPill || (cat.Id == open && ReferenceEquals(_s.CategoryWindow.Owner, this));
            pill.Background = Theme.Brush(pill == _dropPill ? Theme.Hover : on ? Theme.Selected : Microsoft.UI.Colors.Transparent);
        }
    }

    void OpenCategory(Category cat, Border pill)
    {
        _s.CategoryWindow.Show(cat.Id, AnchorOf(pill), this);
        Highlight();
        _s.CategoryWindow.Closed -= Highlight;
        _s.CategoryWindow.Closed += Highlight;
    }

    void OnPillMoved(Category cat)
    {
        if (_pressPill is null) return;
        GetCursorPos(out var c);
        if (_dragCat is null)
        {
            if (Math.Abs(c.X - _pressAt.X) < Px(6)) return;
            _dragCat = cat;
            Popups.CloseAll();
            _pressPill.Opacity = 0.6;
        }
        // Пилюля встаёт туда, где курсор (порядок меняется сразу).
        var others = _pillList.Where(p => p.Cat != _dragCat).ToList();
        var index = others.Count(p => { var a = AnchorOf(p.Pill); return a.X + a.Width / 2 < c.X; });
        var current = _pillList.FindIndex(p => p.Cat == _dragCat);
        if (index == current) return;
        var moving = _pillList[current];
        _pillList.RemoveAt(current);
        _pillList.Insert(index, moving);
        _pills.Children.Remove(moving.Pill);
        _pills.Children.Insert(index, moving.Pill);
    }

    void OnPillReleased(Border pill, Category cat, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        pill.ReleasePointerCaptures();
        var kind = e.GetCurrentPoint(pill).Properties.PointerUpdateKind;
        if (_dragCat != null) { EndPillDrag(commit: true); _pressPill = null; return; }
        var pressed = _pressPill == pill;
        _pressPill = null;
        if (kind == PointerUpdateKind.LeftButtonReleased && pressed)
        {
            // Повторный клик закрывает, клик по другой — переключает одним кликом.
            var key = (this, cat.Id);
            if (Popups.Current == _s.CategoryWindow && Equals(_s.CategoryWindow.Anchor, key)) { _s.CategoryWindow.Close(restoreFocus: true); return; }
            if (Popups.JustClosed(key)) return;
            OpenCategory(cat, pill);
        }
        else if (kind == PointerUpdateKind.RightButtonReleased)
        {
            var key = (this, "menu", cat.Id);
            Popups.Toggle(key, () => _s.Menu.Show(key, AnchorOf(pill), CategoryWindow.CategoryMenu(_s, cat, () => Rename(cat.Id)), Placement.Below, this));
        }
    }

    void EndPillDrag(bool commit)
    {
        var cat = _dragCat;
        _dragCat = null;
        if (cat is null) return;
        var order = _pillList.Select(p => p.Cat.Id).ToList();
        if (commit && !order.SequenceEqual(_s.Config.Categories.Select(c => c.Id)))
            _s.Config.Update(c => c.Categories = order.Select(id => c.Categories.First(x => x.Id == id)).ToList());
        else { _built = ""; Build(); }
    }

    void NewCategory()
    {
        var key = (this, "new");
        Popups.Toggle(key, () => _s.NameFlyout.Show(key, AnchorOf(_add), "Новая категория", "", this, name =>
        {
            string id = "";
            _s.Config.Update(c =>
            {
                id = _s.Config.NewId("category");
                var used = c.Categories.Select(x => x.Color.ToUpperInvariant()).ToHashSet();
                var color = ShellConfig.Colors.FirstOrDefault(x => !used.Contains(x)) ?? ShellConfig.Colors[c.Categories.Count % ShellConfig.Colors.Length];
                c.Categories.Add(new Category { Id = id, Name = name, Color = color });
            });
            // Новая категория сразу открыта: можно бросать в неё файлы.
            var pill = _pillList.FirstOrDefault(p => p.Cat.Id == id);
            if (pill.Pill != null) _s.Queue.TryEnqueue(() => OpenCategory(pill.Cat, pill.Pill));
        }));
    }

    /// Поле переименования под пилюлей категории.
    public void Rename(string categoryId)
    {
        var pill = _pillList.FirstOrDefault(p => p.Cat.Id == categoryId);
        if (pill.Pill is null) return;
        var cat = pill.Cat;
        _s.NameFlyout.Show((this, "rename", cat.Id), AnchorOf(pill.Pill), "Название категории", cat.Name, this,
            name => _s.Config.Update(_ => cat.Name = name));
    }

    // ---------- «Сейчас играет» ----------

    void UpdateMedia()
    {
        var m = _s.Media.Current;
        var want = _s.Config.WidgetEnabled("media") && m != null;
        var has = _row.Children.Contains(_media);
        if (want != has) { _s.Queue.TryEnqueue(Build); return; }
        if (m is null) return;
        _mediaTitle.Text = m.Artist.Length > 0 ? $"{m.Title} — {m.Artist}" : m.Title;
        ToolTipService.SetToolTip(_media, _mediaTitle.Text);
        _mediaCover.Source = _s.Media.Cover;
        _mediaNote.Visibility = _s.Media.Cover is null ? Visibility.Visible : Visibility.Collapsed;
        _mediaPlayIcon.Glyph = m.Playing ? "" : "";
        _mediaPlay.Opacity = m.CanPlayPause ? 1 : 0.4;
        var old = _w;
        Measure();
        if (_w != old) { Place(); _s.Bars.Publish(); }
    }

    // ---------- перетаскивание на пилюли ----------

    void OnFileDragOver(Category cat, Border pill, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.None; return; }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"Добавить в «{cat.Name}»";
        if (_dropPill != pill)
        {
            _dropPill = pill;
            _springCat = cat.Id;
            _springTimer.Stop();
            _springTimer.Start();
            Highlight();
        }
    }

    async void OnFileDrop(Category cat, DragEventArgs e)
    {
        _springTimer.Stop();
        _dropPill = null;
        Highlight();
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var files = await e.DataView.GetStorageItemsAsync();
            var items = files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p))
                .Select(p => ShellConfig.ItemFromPath(ShellConfig.KeepShortcut(p))).ToList();
            if (items.Count > 0) _s.AddToCategory(cat.Id, items);
        }
        catch (Exception ex) { Log.Error("файлы на категорию", ex); }
        finally { deferral.Complete(); }
    }

    (Category Cat, Border Pill)? PillAt(int x, int y)
    {
        if (!_shown || y < _y - _slide || y > _y - _slide + _h) return null;
        foreach (var p in _pillList)
        {
            var a = AnchorOf(p.Pill);
            if (x >= a.X && x < a.X + a.Width) return p;
        }
        return null;
    }

    public bool DragOver(int x, int y, DragPayload payload)
    {
        var hit = PillAt(x, y);
        if (hit is not { } p || p.Cat.Id == payload.FromCategory) { DragLeave(); return false; }
        if (_dropPill != p.Pill) { _dropPill = p.Pill; Highlight(); }
        return true;
    }

    public void DragLeave()
    {
        if (_dropPill is null) return;
        _dropPill = null;
        Highlight();
    }

    public bool Drop(int x, int y, DragPayload payload)
    {
        var hit = PillAt(x, y);
        DragLeave();
        if (hit is not { } p || p.Cat.Id == payload.FromCategory) return false;
        // Плитку перенесли в другую категорию.
        var item = payload.Item;
        _s.Config.Update(c =>
        {
            var from = c.Categories.FirstOrDefault(x => x.Id == payload.FromCategory);
            from?.Items.Remove(item);
            var to = c.Categories.FirstOrDefault(x => x.Id == p.Cat.Id);
            if (to != null && !to.Items.Any(i => string.Equals(i.Target, item.Target, StringComparison.OrdinalIgnoreCase))) to.Items.Add(item);
        });
        return true;
    }
}
