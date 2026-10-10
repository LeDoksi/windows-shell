using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Audio;
using WindowsShell.Config;
using WindowsShell.Native;
using WindowsShell.Ui;
using WinUIEx;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Dock;

/// Док (раздел 1): кнопка Пуска, закреплённые и запущенные приложения, разделитель и хвост
/// (звук, часы). Два окна: снизу таблетка с настоящим стеклом, сверху прозрачное окно с
/// иконками. Увеличение волной считается каждый кадр и двигает только визуалы (без раскладки).
public sealed class Dock
{
    // Размеры в DIP.
    const double Icon = 48, Gap = 6, Pad = 10, TopPad = 9, PillH = 68, Headroom = 96, Bottom = 8;
    const double MaxScale = 1.6, Reach = 3.0, ElementSize = Icon * MaxScale;
    const double SepW = 21, SoundW = 40, ClockW = 74, TailGap = 2;
    const double TailW = SepW + SoundW + TailGap + ClockW;

    readonly WindowTracker _tracker;
    readonly AudioService _audio;
    readonly Icons _icons;
    readonly PrototypeConfig _config;
    readonly ShellWindow _pill;
    readonly ShellWindow _strip;
    readonly Canvas _canvas = new();
    readonly List<Item> _items = [];
    readonly Rectangle _separator;
    readonly Grid _sound;
    readonly FontIcon _soundIcon;
    readonly StackPanel _clock;
    readonly TextBlock _time;
    readonly TextBlock _date;
    readonly Border _tooltip;
    readonly TextBlock _tooltipText;
    readonly MenuPopup _menu = new();
    readonly SoundFlyout _soundFlyout;
    readonly WindowListPopup _list;
    readonly DispatcherQueueTimer _hoverTimer;
    readonly DispatcherQueueTimer _listCloseTimer;
    readonly DispatcherQueueTimer _clockTimer;
    readonly DispatcherQueueTimer _volumeTipTimer;

    // Положение окна-полосы на экране (физические пиксели) и масштаб монитора.
    int _stripX, _stripY, _stripW, _stripH;
    double _scale = 1, _widthDip;

    bool _hover;
    double _cursorX;
    bool _animating;
    TimeSpan _lastFrame;
    (int X, int W) _pillPx = (-1, -1);
    bool _regionIsPill;

    object? _hovered;
    long _tooltipHiddenAt;
    object? _tooltipFor;
    long _lastLeftClick;
    object? _lastLeftTarget;
    object? _pressed;

    sealed class Item
    {
        public required string Key;
        public DockEntry? Entry;
        public bool IsStart;
        public required Grid Visual;
        public required Image? Image;
        /// Значок-заглушка, пока у программы нет своей иконки (не найдена, не отдала иконку).
        public FontIcon? Placeholder;
        public required Border Dot;
        public double Base, BaseV, Size, SizeV;
        public bool Removing;
        public long LaunchUntil;
        public string? IconSource;
        // Положение этого кадра (DIP, в координатах полосы).
        public double X, Top;
    }

    public Dock(WindowTracker tracker, AudioService audio, Icons icons, PrototypeConfig config)
    {
        _tracker = tracker;
        _audio = audio;
        _icons = icons;
        _config = config;
        _soundFlyout = new SoundFlyout(audio, icons);
        _list = new WindowListPopup(tracker, icons);

        _pill = new ShellWindow(new AlwaysActiveAcrylic(dock: true), noActivate: true, rounded: true, transitions: false);
        _strip = new ShellWindow(new TransparentTintBackdrop(), noActivate: true, rounded: false, transitions: false);
        _strip.Root.Children.Add(_canvas);

        _separator = new Rectangle { Width = 1, Height = 34, Fill = Theme.Brush(Theme.Stroke) };
        _canvas.Children.Add(_separator);

        _soundIcon = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 18, Foreground = Theme.Brush(Theme.Text) };
        _sound = new Grid
        {
            Width = SoundW,
            Height = 40,
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            Children = { _soundIcon },
        };
        _canvas.Children.Add(_sound);

        _time = new TextBlock { FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), HorizontalAlignment = HorizontalAlignment.Right };
        _date = new TextBlock { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextSecondary), HorizontalAlignment = HorizontalAlignment.Right };
        _clock = new StackPanel { Width = ClockW, Padding = new Thickness(0, 0, 6, 0), Children = { _time, _date } };
        _canvas.Children.Add(_clock);

        _tooltipText = new TextBlock { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.Text) };
        _tooltip = new Border
        {
            Background = Theme.Brush(Theme.Tooltip),
            BorderBrush = Theme.Brush(Theme.Stroke),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 5, 9, 6),
            Child = _tooltipText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_tooltip);

        var q = DispatcherQueue.GetForCurrentThread();
        _hoverTimer = q.CreateTimer();
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (_, _) => OnHoverTimer();
        _listCloseTimer = q.CreateTimer();
        _listCloseTimer.Interval = TimeSpan.FromMilliseconds(300);
        _listCloseTimer.IsRepeating = false;
        _listCloseTimer.Tick += (_, _) =>
        {
            if (_list.ContainsCursor()) return;
            if (_hovered is Item it && it.Entry?.Windows.Count >= 2 && _list.IsOpen && Equals(_list.Anchor, it.Key)) return;
            _list.Close(restoreFocus: false);
        };
        _clockTimer = q.CreateTimer();
        _clockTimer.IsRepeating = false;
        _clockTimer.Tick += (_, _) => UpdateClock();
        _volumeTipTimer = q.CreateTimer();
        _volumeTipTimer.IsRepeating = false;
        _volumeTipTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _volumeTipTimer.Tick += (_, _) => { if (Equals(_tooltipFor, "volume")) HideTooltip(); };

        _strip.Root.PointerMoved += OnPointerMoved;
        _strip.Root.PointerExited += (_, _) => SetHover(false);
        _strip.Root.PointerPressed += OnPointerPressed;
        _strip.Root.PointerReleased += OnPointerReleased;
        _strip.Root.PointerWheelChanged += OnWheel;
        _list.PointerLeft += () => { _listCloseTimer.Stop(); _listCloseTimer.Start(); };

        // Клик по доку не должен забирать фокус у окна, в котором работаешь (правило 0.6).
        _strip.Window.Activated += (_, e) => GuardFocus(e, _strip.Hwnd);
        _pill.Window.Activated += (_, e) => GuardFocus(e, _pill.Hwnd);

        _tracker.Changed += Rebuild;
        _tracker.ForegroundChanged += () =>
        {
            // Активным стало чужое окно: наше окошко закрывается (страховка к 0.4, если Windows
            // не дала окошку стать активным).
            // События приходят с опозданием: смотрим, чьё окно активно сейчас.
            if (Popups.Current is { IsOpen: true, Activating: true } p && ProcessId(GetForegroundWindow()) != (uint)Environment.ProcessId)
                p.Close(restoreFocus: false);
            RaiseAll();
            UpdateDots();
        };
        _audio.Changed += UpdateSound;
        Popup.RestoreForeground = _tracker.RestoreForeground;
    }

    void GuardFocus(WindowActivatedEventArgs e, IntPtr hwnd)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated) return;
        if (GetForegroundWindow() == hwnd) _tracker.RestoreForeground();
    }

    // ---------- окно и место ----------

    public void Show()
    {
        var mon = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        var work = MonitorInfo(mon).rcWork;
        _scale = MonitorScale(mon);
        _stripW = work.Width;
        _stripH = (int)Math.Ceiling((Headroom + PillH + Bottom) * _scale);
        _stripX = work.Left;
        _stripY = work.Bottom - _stripH;
        _widthDip = _stripW / _scale;

        Rebuild(initial: true);
        UpdateSound();
        UpdateClock();
        Frame(0);

        _pill.Place(PillScreenX(), _stripY + Px(Headroom), Px(PillWidth()), Px(PillH));
        _strip.Place(_stripX, _stripY, _stripW, _stripH);
        _pill.Show(activate: false);
        _strip.Show(activate: false);
        RaiseAll();
        SetRegion(pill: true);
    }

    int Px(double dip) => (int)Math.Round(dip * _scale);

    /// Подложка ниже иконок, окошки выше дока (правило 0.10).
    void RaiseAll()
    {
        _pill.Raise();
        _strip.Raise();
        if (Popups.Current is { IsOpen: true } p) SetWindowPos(p.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ---------- модель ----------

    void Rebuild() => Rebuild(initial: false);

    void Rebuild(bool initial)
    {
        var entries = DockModel.Build(_config.Pins, _tracker.Windows);
        var old = _items.ToList();
        var next = new List<Item>();
        var start = old.FirstOrDefault(i => i.IsStart) ?? CreateStart();
        next.Add(start);
        foreach (var e in entries)
        {
            var item = old.FirstOrDefault(i => i.Key == e.Id && !i.Removing);
            if (item == null)
            {
                item = CreateItem(e.Id);
                if (initial) item.Base = item.Size = Icon;
            }
            item.Entry = e;
            if (e.Running) item.LaunchUntil = 0;
            SetIcon(item, IconSource(e), e.Windows.FirstOrDefault()?.Exe ?? Launcher.Resolve(e.Target).Exe);
            item.Visual.Opacity = e.Missing ? 0.4 : 1;
            next.Add(item);
        }
        // Закрыл последнее окно незакреплённой программы: иконка плавно исчезает на своём месте.
        for (var i = 0; i < old.Count; i++)
        {
            var o = old[i];
            if (next.Contains(o)) continue;
            o.Removing = true;
            next.Insert(Math.Min(i, next.Count), o);
        }
        _items.Clear();
        _items.AddRange(next);
        UpdateDots();
        Kick();
        // Открытый список окон показывает живой список.
        if (_list.IsOpen && _items.FirstOrDefault(i => Equals(i.Key, _list.Anchor)) is { Entry: { } le } && le.Windows.Count != _list.Windows.Count)
        {
            if (le.Windows.Count >= 2) ShowWindowList(_items.First(i => Equals(i.Key, _list.Anchor)));
            else _list.Close(restoreFocus: false);
        }
    }

    static string IconSource(DockEntry e)
    {
        if (e.Pinned) return Launcher.ShellName(e.Target);
        var w = e.Windows.FirstOrDefault();
        if (w?.Aumid != null && !Launcher.IsPath(e.Target)) return $"shell:AppsFolder\\{w.Aumid}";
        return Launcher.ShellName(e.Target);
    }

    Item CreateItem(string key)
    {
        var image = new Image { Width = ElementSize, Height = ElementSize, Stretch = Stretch.Uniform };
        var placeholder = new FontIcon
        {
            Glyph = "\uECAA",
            FontFamily = Theme.Icons,
            FontSize = ElementSize * 0.6,
            Foreground = Theme.Brush(Theme.TextSecondary),
            Visibility = Visibility.Collapsed,
        };
        var visual = new Grid { Width = ElementSize, Height = ElementSize, Children = { placeholder, image } };
        var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Theme.Brush(Theme.Text), Opacity = 0 };
        _canvas.Children.Insert(0, dot);
        _canvas.Children.Insert(0, visual);
        return new Item { Key = key, Visual = visual, Image = image, Dot = dot, Placeholder = placeholder };
    }

    Item CreateStart()
    {
        // Логотип Windows из четырёх квадратов в нашем акценте.
        var logo = new Grid { Width = 26, Height = 26, ColumnSpacing = 2, RowSpacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        logo.ColumnDefinitions.Add(new ColumnDefinition());
        logo.ColumnDefinitions.Add(new ColumnDefinition());
        logo.RowDefinitions.Add(new RowDefinition());
        logo.RowDefinitions.Add(new RowDefinition());
        for (var r = 0; r < 2; r++)
        for (var c = 0; c < 2; c++)
        {
            var sq = new Rectangle { Fill = Theme.Brush(Theme.Accent), RadiusX = 1.5, RadiusY = 1.5 };
            Grid.SetRow(sq, r);
            Grid.SetColumn(sq, c);
            logo.Children.Add(sq);
        }
        var plate = new Grid
        {
            Width = Icon,
            Height = Icon,
            CornerRadius = new CornerRadius(12),
            Background = Theme.Brush(Windows.UI.Color.FromArgb(0x14, 255, 255, 255)),
            Children = { logo },
        };
        var visual = new Grid { Width = Icon, Height = Icon, Children = { plate } };
        var dot = new Border { Width = 0, Height = 0, Opacity = 0 };
        _canvas.Children.Insert(0, visual);
        return new Item { Key = "start", IsStart = true, Visual = visual, Image = null, Dot = dot, Base = Icon, Size = Icon };
    }

    /// Иконка по AUMID или пути; если Проводник её не знает (свой AUMID у Electron-программ),
    /// берём иконку exe.
    void SetIcon(Item item, string source, string? fallback)
    {
        if (item.IconSource == source || item.Image is null) return;
        item.IconSource = source;
        var img = item.Image;
        void Apply(ImageSource? bmp)
        {
            img.Source = bmp;
            if (item.Placeholder != null) item.Placeholder.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
        }
        _icons.Get(source, bmp =>
        {
            if (item.IconSource != source) return;
            if (bmp != null || fallback is null || string.Equals(fallback, source, StringComparison.OrdinalIgnoreCase)) { Apply(bmp); return; }
            _icons.Get(fallback, alt => { if (item.IconSource == source) Apply(alt); });
        });
    }

    bool IsActive(DockEntry e) => e.Windows.Any(w => w.Hwnd == _tracker.Foreground);

    void UpdateDots()
    {
        foreach (var it in _items)
        {
            if (it.IsStart || it.Entry is null) continue;
            var running = it.Entry.Running;
            var active = running && IsActive(it.Entry);
            it.Dot.Width = active ? 14 : 5;
            it.Dot.Height = active ? 4 : 5;
            it.Dot.CornerRadius = new CornerRadius(active ? 2 : 2.5);
            it.Dot.Opacity = !running ? 0 : active ? 1 : 0.55;
        }
        Kick();
    }

    // ---------- кадр ----------

    void Kick()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    void OnRendering(object? sender, object e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((t - _lastFrame).TotalSeconds, 0, 0.05);
        _lastFrame = t;
        if (!Frame(dt))
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
            if (!_hover) SetRegion(pill: true);
        }
    }

    static double Magnify(double distanceInSlots) =>
        distanceInSlots >= Reach ? 1 : 1 + (MaxScale - 1) * (1 + Math.Cos(Math.PI * distanceInSlots / Reach)) / 2;

    /// Пружина без перелёта: плавно и без дрожания в конце (правило 0.8). true — ещё движется.
    static bool Spring(ref double x, ref double v, double target, double dt, double k)
    {
        if (dt <= 0) return Math.Abs(x - target) > 0.05;
        var c = 2 * Math.Sqrt(k);
        var n = (int)Math.Ceiling(dt / 0.004);
        var h = dt / n;
        for (var i = 0; i < n; i++)
        {
            var a = k * (target - x) - c * v;
            v += a * h;
            x += v * h;
        }
        if (Math.Abs(x - target) < 0.05 && Math.Abs(v) < 1) { x = target; v = 0; return false; }
        return true;
    }

    double BaseTotal() => Pad + _items.Sum(i => i.Base + Gap * i.Base / Icon) - (_items.Count > 0 ? Gap : 0) + TailW + Pad;

    double PillLeft;
    double PillWidth() => BaseTotal() + _items.Sum(i => i.Size - i.Base);
    int PillScreenX() => _stripX + Px(PillLeft);

    /// Один кадр: пружины, раскладка, подложка. Возвращает true, пока что-то движется.
    bool Frame(double dt)
    {
        var animate = AnimationsEnabled();
        var moving = false;
        var now = Environment.TickCount64;

        // Базовые размеры: появление и исчезновение иконок.
        foreach (var it in _items)
        {
            var target = it.Removing ? 0 : Icon;
            if (!animate || dt == 0 && it.Base == target) { it.Base = target; it.BaseV = 0; }
            else moving |= Spring(ref it.Base, ref it.BaseV, target, dt, 500);
        }
        foreach (var gone in _items.Where(i => i.Removing && i.Base <= 0.01).ToList())
        {
            _items.Remove(gone);
            _canvas.Children.Remove(gone.Visual);
            _canvas.Children.Remove(gone.Dot);
        }

        // Базовая раскладка (без увеличения) по центру полосы.
        var baseTotal = BaseTotal();
        var baseLeft = (_widthDip - baseTotal) / 2;
        var bl = new double[_items.Count];
        var x = baseLeft + Pad;
        for (var i = 0; i < _items.Count; i++)
        {
            bl[i] = x;
            x += _items[i].Base + Gap * _items[i].Base / Icon;
        }

        // Цели увеличения: волна от курсора по базовым центрам.
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var center = bl[i] + it.Base / 2;
            var target = it.Base * (_hover ? Magnify(Math.Abs(_cursorX - center) / (Icon + Gap)) : 1);
            if (!animate) { it.Size = target; it.SizeV = 0; }
            else moving |= Spring(ref it.Size, ref it.SizeV, target, dt, 700);
        }

        // Точка под курсором стоит на месте: таблетка растёт влево и вправо по-разному, как у Mac.
        double extraLeft = 0, extraAll = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var e = it.Size - it.Base;
            extraAll += e;
            var frac = it.Base <= 0 ? (_cursorX > bl[i] ? 1 : 0) : Math.Clamp((_cursorX - bl[i]) / it.Base, 0, 1);
            extraLeft += e * frac;
        }
        var pillW = baseTotal + extraAll;
        PillLeft = Math.Clamp(baseLeft - extraLeft, 4, Math.Max(4, _widthDip - pillW - 4));

        // Иконки снизу по одной линии: растут вверх, низ стоит на месте.
        var iconBottom = Headroom + TopPad + Icon;
        x = PillLeft + Pad;
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            it.X = x;
            it.Top = iconBottom - it.Size;
            var visualSize = it.IsStart ? Icon : ElementSize;
            var s = (float)(it.Size / visualSize);
            it.Visual.Translation = new Vector3((float)x, (float)it.Top, 0);
            it.Visual.Scale = new Vector3(s, s, 1);
            var opacity = it.Entry?.Missing == true ? 0.4 : 1.0;
            if (it.LaunchUntil > now && it.Entry is { Running: false })
            {
                // Программа запускается: мягкая пульсация до появления окна, максимум 10 с.
                opacity = 0.6 + 0.4 * (0.5 + 0.5 * Math.Cos(now / 1000.0 * 2 * Math.PI / 1.1));
                moving = true;
            }
            it.Visual.Opacity = opacity;
            it.Dot.Translation = new Vector3((float)(x + it.Size / 2 - it.Dot.Width / 2), (float)(Headroom + PillH - 7 - it.Dot.Height / 2), 0);
            x += it.Size + Gap * it.Base / Icon;
        }
        x -= Gap;

        // Хвост: разделитель, звук, часы.
        var tailY = Headroom + PillH / 2;
        _separator.Translation = new Vector3((float)(x + 10), (float)(tailY - 17), 0);
        x += SepW;
        _sound.Translation = new Vector3((float)x, (float)(tailY - 20), 0);
        x += SoundW + TailGap;
        _clock.Translation = new Vector3((float)x, (float)(tailY - 18), 0);

        // Подсказка едет за своей иконкой.
        if (_tooltip.Visibility == Visibility.Visible) PlaceTooltip();

        // Подложка со стеклом повторяет таблетку.
        var px = (PillScreenX(), Px(pillW));
        if (px != _pillPx && _pill.Visible)
        {
            _pillPx = px;
            _pill.Move(px.Item1, _stripY + Px(Headroom), px.Item2, Px(PillH));
        }
        return moving;
    }

    /// Пока курсор не над доком, окно-полоса ловит мышь только на таблетке, а прозрачное
    /// место над ней пропускает клики к окнам под ним.
    void SetRegion(bool pill)
    {
        if (pill)
        {
            var x = Px(PillLeft);
            var rgn = CreateRectRgn(x, Px(Headroom), x + Px(PillWidth()), _stripH);
            SetWindowRgn(_strip.Hwnd, rgn, true);
            _regionIsPill = true;
        }
        else if (_regionIsPill)
        {
            // Всё окно, кроме верхних 2 px: там окно WinUI без заголовка рисует тонкую белую
            // полоску рамки, и у прозрачного окна во всю ширину она видна поперёк экрана.
            SetWindowRgn(_strip.Hwnd, CreateRectRgn(0, Px(2), _stripW, _stripH), true);
            _regionIsPill = false;
        }
    }

    // ---------- мышь ----------

    void SetHover(bool on)
    {
        if (_hover == on) return;
        _hover = on;
        if (on) SetRegion(pill: false);
        else
        {
            SetHovered(null);
            // Сразу отпускаем место над доком, где нет иконок.
            var x = Px(PillLeft);
            var rgn = CreateRectRgn(x, Px(Headroom - Icon * (MaxScale - 1)), x + Px(PillWidth()), _stripH);
            SetWindowRgn(_strip.Hwnd, rgn, true);
            _regionIsPill = true;
        }
        Kick();
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        var pillW = PillWidth();
        var inside = p.X >= PillLeft && p.X <= PillLeft + pillW && p.Y >= Headroom - Icon * (MaxScale - 1) - 2;
        if (inside) _cursorX = p.X;
        SetHover(inside);
        SetHovered(inside ? HitTest(p) : null);
        if (inside) Kick();
    }

    object? HitTest(Point p)
    {
        foreach (var it in _items)
        {
            if (it.Removing) continue;
            if (p.X >= it.X - Gap / 2 && p.X < it.X + it.Size + Gap / 2 && p.Y >= it.Top - 4) return it;
        }
        var s = _sound.Translation;
        if (p.X >= s.X && p.X < s.X + SoundW && p.Y >= Headroom) return _sound;
        var c = _clock.Translation;
        if (p.X >= c.X && p.X < c.X + ClockW && p.Y >= Headroom) return _clock;
        return null;
    }

    void SetHovered(object? target)
    {
        if (Equals(_hovered, target)) return;
        var hadTooltip = _tooltip.Visibility == Visibility.Visible && !Equals(_tooltipFor, "volume");
        _hovered = target;
        _hoverTimer.Stop();
        _sound.Background = Theme.Brush(ReferenceEquals(target, _sound) ? Theme.Hover : Microsoft.UI.Colors.Transparent);
        if (!Equals(_tooltipFor, "volume")) HideTooltip();
        if (target is null)
        {
            if (_list.IsOpen) { _listCloseTimer.Stop(); _listCloseTimer.Start(); }
            return;
        }
        // Открытый список окон сразу перескакивает на соседнюю иконку.
        if (target is Item { Entry.Windows.Count: >= 2 } it && _list.IsOpen)
        {
            _listCloseTimer.Stop();
            if (!Equals(_list.Anchor, it.Key)) ShowWindowList(it);
            return;
        }
        if (_list.IsOpen) { _listCloseTimer.Stop(); _listCloseTimer.Start(); }
        // Если подсказка уже была видна, соседняя появляется сразу (правило 0.9).
        var recent = hadTooltip || Environment.TickCount64 - _tooltipHiddenAt < 150;
        var wantsList = target is Item { Entry.Windows.Count: >= 2 };
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(wantsList ? 500 : recent ? 1 : 400);
        _hoverTimer.Start();
    }

    void OnHoverTimer()
    {
        if (_hovered is null || _pressed != null) return;
        if (Popups.Current is { } p && p != _list && p.IsOpen) return;
        if (_hovered is Item { Entry.Windows.Count: >= 2 } it) { ShowWindowList(it); return; }
        ShowTooltip(_hovered, TooltipText(_hovered));
    }

    string TooltipText(object target) => target switch
    {
        Item { IsStart: true } => "Пуск",
        Item { Entry: { Missing: true } } => "Не найдено",
        Item { Entry: { Windows.Count: 1 } e } => e.Windows[0].Title,
        Item { Entry: { } e } => e.Title,
        _ when ReferenceEquals(target, _sound) => _audio.Muted ? "Звук выключен" : $"Громкость {_audio.Volume} %",
        _ when ReferenceEquals(target, _clock) => DateTime.Now.ToString("dddd, d MMMM yyyy"),
        _ => "",
    };

    void ShowTooltip(object target, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _tooltipFor = target;
        _tooltipText.Text = text.Length > 60 ? text[..57] + "…" : text;
        _tooltip.Visibility = Visibility.Visible;
        PlaceTooltip();
    }

    void PlaceTooltip()
    {
        _tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _tooltip.DesiredSize;
        double cx, top;
        switch (_tooltipFor)
        {
            case Item it: cx = it.X + it.Size / 2; top = it.Top; break;
            case "volume": cx = _sound.Translation.X + SoundW / 2; top = Headroom; break;
            case Grid g when ReferenceEquals(g, _sound): cx = _sound.Translation.X + SoundW / 2; top = Headroom; break;
            default: cx = _clock.Translation.X + ClockW / 2; top = Headroom; break;
        }
        var x = Math.Clamp(cx - size.Width / 2, 2, _widthDip - size.Width - 2);
        _tooltip.Translation = new Vector3((float)x, (float)(top - 8 - size.Height), 0);
    }

    void HideTooltip()
    {
        if (_tooltip.Visibility == Visibility.Visible) _tooltipHiddenAt = Environment.TickCount64;
        _tooltip.Visibility = Visibility.Collapsed;
        _tooltipFor = null;
    }

    Rect AnchorPx(object target)
    {
        double x, w;
        switch (target)
        {
            // Над увеличенной иконкой, как у Mac.
            case Item it: return new Rect(_stripX + it.X * _scale, _stripY + it.Top * _scale, it.Size * _scale, (Headroom + PillH - it.Top) * _scale);
            case Grid g when ReferenceEquals(g, _sound): x = _sound.Translation.X; w = SoundW; break;
            default: x = _clock.Translation.X; w = ClockW; break;
        }
        return new Rect(_stripX + x * _scale, _stripY + Headroom * _scale, w * _scale, PillH * _scale);
    }

    void ShowWindowList(Item it)
    {
        if (it.Entry is null) return;
        HideTooltip();
        _listCloseTimer.Stop();
        _list.Show(it.Key, AnchorPx(it), it.Entry.Windows, it.IconSource ?? "");
    }

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        _pressed = HitTest(p.Position);
        _hoverTimer.Stop();
        if (!Equals(_tooltipFor, "volume")) HideTooltip();
        e.Handled = true;
    }

    void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        var target = HitTest(p.Position);
        var pressed = _pressed;
        _pressed = null;
        e.Handled = true;
        if (target is null || !Equals(target, pressed)) return;
        switch (p.Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.LeftButtonReleased:
                // Двойной клик = один клик: второй в течение 300 мс не считается.
                var now = Environment.TickCount64;
                if (Equals(_lastLeftTarget, target) && now - _lastLeftClick < 300) return;
                _lastLeftTarget = target;
                _lastLeftClick = now;
                LeftClick(target);
                break;
            case PointerUpdateKind.RightButtonReleased:
                RightClick(target);
                break;
            case PointerUpdateKind.MiddleButtonReleased:
                MiddleClick(target);
                break;
        }
    }

    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var p = e.GetCurrentPoint(_canvas);
        if (!ReferenceEquals(HitTest(p.Position), _sound)) return;
        var delta = p.Properties.MouseWheelDelta;
        if (delta == 0) return;
        _audio.Volume = _audio.Volume + Math.Sign(delta) * 2;
        _hoverTimer.Stop();
        ShowTooltip("volume", $"{_audio.Volume} %");
        _volumeTipTimer.Stop();
        _volumeTipTimer.Start();
    }

    // ---------- клики ----------

    void LeftClick(object target)
    {
        _list.Close(restoreFocus: false);
        if (ReferenceEquals(target, _sound))
        {
            Popups.Toggle(_sound, () => _soundFlyout.Show(_sound, AnchorPx(_sound)));
            return;
        }
        if (target is not Item it) return;
        // Один клик = одно действие: открытое меню закрывается, и клик срабатывает.
        if (Popups.Current is { IsOpen: true }) Popups.CloseAll(restoreFocus: true);
        if (it.IsStart)
        {
            // Свой Пуск будет в WSH-E15; в прототипе открываем родной.
            keybd_event(VK_LWIN, 0, 0, IntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            return;
        }
        var entry = it.Entry!;
        var shift = KeyDown(VK_SHIFT);
        var ctrl = KeyDown(VK_CONTROL);
        if (entry.Missing)
        {
            ShowTooltip(it, "Программа не найдена. Убрать из дока: правый клик");
            return;
        }
        if (shift && ctrl) { Launch(it, admin: true); return; }
        if (shift) { Launch(it); return; }
        var recent = entry.Windows.FirstOrDefault();
        if (recent is null) { Launch(it); return; }
        if (recent.Hwnd == _tracker.Foreground && !recent.Minimized) _tracker.Minimize(recent.Hwnd);
        else _tracker.Activate(recent.Hwnd);
    }

    void Launch(Item it, bool admin = false)
    {
        if (it.Entry is null) return;
        it.LaunchUntil = Environment.TickCount64 + 10_000;
        Launcher.LaunchInBackground(it.Entry.Target, admin);
        Kick();
    }

    void MiddleClick(object target)
    {
        if (ReferenceEquals(target, _sound)) { _audio.Muted = !_audio.Muted; return; }
        if (target is Item { IsStart: false, Entry.Missing: false } it) Launch(it);
    }

    void RightClick(object target)
    {
        _list.Close(restoreFocus: false);
        HideTooltip();
        if (ReferenceEquals(target, _sound))
        {
            Popups.Toggle(("menu", _sound), () => _menu.Show(("menu", _sound), AnchorPx(_sound), SoundMenu()));
            return;
        }
        if (target is not Item it) return;
        var key = ("menu", it.Key);
        // Правый клик по другой иконке при открытом меню: меню перескакивает одним кликом.
        Popups.Toggle(key, () => _menu.Show(key, AnchorPx(it), it.IsStart ? StartMenu() : AppMenu(it)));
    }

    List<MenuItem> StartMenu() =>
    [
        new() { Text = "Диспетчер задач", Glyph = "", Invoke = () => Launcher.Open("taskmgr.exe") },
        new() { Text = "Параметры", Glyph = "", Invoke = () => Launcher.Open("ms-settings:") },
        new() { Text = "Проводник", Glyph = "", Invoke = () => Launcher.Open("explorer.exe") },
        MenuItem.Sep,
        new() { Text = "Выйти из прототипа", Glyph = "", Invoke = Quit },
    ];

    /// Выход: окна дока убираются сразу, процесс завершается (Application.Exit оставлял
    /// окно-полосу на экране и процесс живым).
    void Quit()
    {
        Popups.CloseAll();
        _list.Close(restoreFocus: false);
        CompositionTarget.Rendering -= OnRendering;
        _pill.Hide();
        _strip.Hide();
        Log.Write("выход");
        Environment.Exit(0);
    }

    List<MenuItem> SoundMenu()
    {
        var current = _audio.DefaultId();
        return
        [
            new()
            {
                Text = "Устройства вывода",
                Glyph = "",
                Children = _audio.Devices().Select(d => new MenuItem { Text = d.Name, Checked = d.Id == current, Invoke = () => _audio.SetDefault(d.Id) }).ToList(),
            },
            new() { Text = "Микшер громкости", Glyph = "", Invoke = () => Launcher.Open("ms-settings:apps-volume") },
            new() { Text = "Параметры звука", Glyph = "", Invoke = () => Launcher.Open("ms-settings:sound") },
        ];
    }

    List<MenuItem> AppMenu(Item it)
    {
        var e = it.Entry!;
        var items = new List<MenuItem>();
        if (e.Running)
        {
            foreach (var w in e.Windows.Take(12))
            {
                var h = w.Hwnd;
                items.Add(new MenuItem { Text = w.Title, Checked = h == _tracker.Foreground, Invoke = () => _tracker.Activate(h) });
            }
            items.Add(MenuItem.Sep);
        }
        items.Add(new MenuItem { Text = e.Running ? "Новое окно" : "Открыть", Glyph = e.Running ? "" : "", Enabled = !e.Missing, Invoke = () => Launch(it) });
        items.Add(new MenuItem { Text = "Запустить от имени администратора", Glyph = "", Enabled = !e.Missing, Invoke = () => Launch(it, admin: true) });
        var location = Launcher.IsPath(e.Target) ? e.Target : Launcher.Resolve(e.Target).Exe ?? e.Windows.FirstOrDefault()?.Exe;
        items.Add(new MenuItem
        {
            Text = "Открыть расположение файла",
            Glyph = "",
            Enabled = location != null && File.Exists(location),
            Invoke = () => { if (location != null) Launcher.Reveal(location); },
        });
        items.Add(MenuItem.Sep);
        if (e.Pinned)
            items.Add(new MenuItem { Text = "Убрать из дока", Glyph = "", Invoke = () => Unpin(e) });
        else
            items.Add(new MenuItem { Text = "Закрепить в доке", Glyph = "", Invoke = () => Pin(e) });
        items.Add(new MenuItem
        {
            Text = "Добавить в категорию",
            Glyph = "",
            Children = _config.Categories.Select(kv => new MenuItem
            {
                Text = kv.Key,
                Checked = kv.Value.Contains(e.Target, StringComparer.OrdinalIgnoreCase),
                Invoke = () => AddToCategory(kv.Key, e.Target),
            }).ToList(),
        });
        if (e.Running)
        {
            items.Add(MenuItem.Sep);
            var windows = e.Windows.Select(w => w.Hwnd).ToList();
            items.Add(new MenuItem
            {
                Text = windows.Count == 1 ? "Закрыть окно" : $"Закрыть все окна ({windows.Count})",
                Glyph = "",
                Danger = true,
                Invoke = () => { foreach (var h in windows) _tracker.Close(h); },
            });
        }
        return items;
    }

    void Pin(DockEntry e)
    {
        _config.Pins.Add(new Pin(Guid.NewGuid().ToString("N")[..8], e.Title, e.Target));
        _config.Save();
        Rebuild();
    }

    void Unpin(DockEntry e)
    {
        _config.Pins.RemoveAll(p => p.Id == e.Id);
        _config.Save();
        Rebuild();
    }

    void AddToCategory(string name, string target)
    {
        var list = _config.Categories[name];
        if (list.Contains(target, StringComparer.OrdinalIgnoreCase)) list.RemoveAll(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));
        else list.Add(target);
        _config.Save();
    }

    // ---------- хвост ----------

    void UpdateSound()
    {
        var v = _audio.Volume;
        _soundIcon.Glyph = !_audio.HasOutput ? "" : _audio.Muted ? "" : v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
        if (Equals(_tooltipFor, "volume")) { _tooltipText.Text = $"{v} %"; PlaceTooltip(); }
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString("HH:mm");
        _date.Text = now.ToString("dd.MM.yyyy");
        _clockTimer.Interval = TimeSpan.FromSeconds(60 - now.Second + 0.05);
        _clockTimer.Start();
    }
}
