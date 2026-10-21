using System.Numerics;
using Microsoft.UI.Dispatching;
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
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Dock;

/// Док (раздел 1) на одном мониторе: кнопка Пуска, закреплённые и запущенные приложения,
/// разделитель и хвост (звук и часы на мониторе хвоста, на остальных только часы). Два окна:
/// снизу таблетка с настоящим стеклом, сверху прозрачное окно с иконками. Увеличение волной,
/// перетаскивание и выезд считаются каждый кадр и двигают только визуалы (без раскладки).
public sealed partial class Dock : IBar, IDropZone
{
    // Размеры в DIP.
    const double Icon = 48, Gap = 6, Pad = 10, TopPad = 9, PillH = 68, Headroom = 96, Bottom = 8;
    const double MaxScale = 1.6, Reach = 3.0, ElementSize = Icon * MaxScale;
    const double SepW = 21, TailGap = 2;
    /// Потянул иконку выше таблетки на столько — «Убрать».
    const double RemoveDistance = 80;

    readonly Services _s;
    readonly ShellWindow _pill;
    readonly ShellWindow _strip;
    readonly Canvas _canvas = new();
    readonly List<Item> _items = [];
    readonly Rectangle _separator;
    readonly Border _tooltip;
    readonly TextBlock _tooltipText;
    readonly Border _removeLabel;
    readonly DispatcherQueueTimer _hoverTimer;
    readonly DispatcherQueueTimer _listCloseTimer;
    readonly DispatcherQueueTimer _clockTimer;
    readonly DispatcherQueueTimer _volumeTipTimer;
    readonly DispatcherQueueTimer _hoverCheck;
    readonly DispatcherQueueTimer _springLoad;

    Screen _screen;
    bool _fullTail;

    // Положение окна-полосы на экране (физические пиксели), масштаб монитора и выезд.
    int _stripX, _stripY, _stripW, _stripH;
    double _scale = 1, _widthDip;
    int _slide;
    double _reveal;
    bool _windowsShown;

    bool _hover;
    double _cursorX;
    bool _animating;
    TimeSpan _lastFrame;
    (int X, int Y, int W) _pillPx = (-1, -1, -1);
    bool _regionIsPill;

    object? _hovered;
    long _tooltipHiddenAt;
    object? _tooltipFor;
    long _lastLeftClick;
    object? _lastLeftTarget;
    object? _pressed;

    // Перетаскивание иконки внутри дока (1.5).
    Item? _dragCandidate;
    Point _pressPoint;
    Item? _drag;
    int _dragFrom;
    double _grabX, _grabY;
    Point _dragPoint;
    bool _removeMode;
    long _reorderUntil;

    // Перетаскивание снаружи (Проводник, плитка категории): промежуток между иконками.
    int _gapIndex = -1;
    double _gapW, _gapV;
    Item? _dropOnto;
    List<string>? _dragPaths;
    bool _dragPrograms = true;

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
        /// Счётчик программы (значок поверх иконки) и полоска загрузки (раздел 8).
        public Image? Badge;
        public Grid? Progress;
        public Border? ProgressFill;
        public double Base, BaseV, Size, SizeV;
        public double DX, DXV;
        public bool Placed;
        public double Bounce, BounceV;
        public bool Attention;
        public bool Removing;
        public long LaunchUntil;
        public string? IconSource;
        /// Своей иконки нет (заглушка, запасная или значок): спросим снова при запуске программы
        /// и не чаще раза в 20 с (WSH-102).
        public bool IconWeak;
        public long IconAt;
        // Положение этого кадра (DIP, в координатах полосы).
        public double X, Top;
    }

    public Dock(Services services, Screen screen, bool fullTail)
    {
        _s = services;
        _screen = screen;
        _fullTail = fullTail;

        _pill = new ShellWindow(Surface.Dock, noActivate: true, rounded: true, transitions: false);
        _strip = new ShellWindow(Surface.Transparent, noActivate: true, rounded: false, transitions: false);
        _strip.Root.Children.Add(_canvas);

        _separator = new Rectangle { Width = 1, Height = 34, Fill = Theme.Brush(Theme.Stroke) };
        _canvas.Children.Add(_separator);

        CreateTail();

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

        _removeLabel = new Border
        {
            Background = Theme.Brush(Theme.Tooltip),
            BorderBrush = Theme.Brush(Theme.Stroke),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 3, 9, 4),
            Child = new TextBlock { Text = "Убрать", FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.Text) },
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_removeLabel);

        var q = _s.Queue;
        _hoverTimer = q.CreateTimer();
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (_, _) => OnHoverTimer();
        _listCloseTimer = q.CreateTimer();
        _listCloseTimer.Interval = TimeSpan.FromMilliseconds(300);
        _listCloseTimer.IsRepeating = false;
        _listCloseTimer.Tick += (_, _) =>
        {
            var list = _s.WindowList;
            if (list.ContainsCursor() || !Popups.OwnedBy(this)) return;
            if (_hovered is Item it && it.Entry?.Windows.Count >= 2 && list.IsOpen && Equals(list.Anchor, (this, it.Key))) return;
            if (list.IsOpen && ReferenceEquals(list.Owner, this)) list.Close(restoreFocus: false);
        };
        _clockTimer = q.CreateTimer();
        _clockTimer.IsRepeating = false;
        _clockTimer.Tick += (_, _) => UpdateClock();
        _hoverCheck = q.CreateTimer();
        _hoverCheck.Interval = TimeSpan.FromMilliseconds(250);
        _hoverCheck.Tick += (_, _) => LeaveIfOutside();
        _volumeTipTimer = q.CreateTimer();
        _volumeTipTimer.IsRepeating = false;
        _volumeTipTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _volumeTipTimer.Tick += (_, _) => { if (Equals(_tooltipFor, "volume")) HideTooltip(); };
        // Файл держат над иконкой программы 600 мс: её окно выходит вперёд (1.5).
        _springLoad = q.CreateTimer();
        _springLoad.IsRepeating = false;
        _springLoad.Interval = TimeSpan.FromMilliseconds(600);
        _springLoad.Tick += (_, _) =>
        {
            // Файл держат над кнопкой Пуска: Пуск открывается, файл можно бросить в категорию (v2 п. 1).
            if (_dropOnto is { IsStart: true }) { if (!_s.StartMenu.IsOpen) _s.ToggleStart(this); return; }
            if (_dropOnto?.Entry?.Windows.FirstOrDefault() is { } w) _s.Tracker.Activate(w.Hwnd);
        };

        _strip.Root.PointerMoved += OnPointerMoved;
        // Смена региона окна под неподвижным курсором тоже присылает «мышь ушла»: веря ей, док
        // гасил увеличение через долю секунды. Уход проверяем по настоящему курсору.
        _strip.Root.PointerExited += (_, _) => { if (_drag is null) LeaveIfOutside(); };
        _strip.Root.PointerPressed += OnPointerPressed;
        _strip.Root.PointerReleased += OnPointerReleased;
        _strip.Root.PointerCaptureLost += (_, _) => { if (_drag != null) EndDrag(commit: false); _dragCandidate = null; };
        _strip.Root.PointerWheelChanged += OnWheel;
        _strip.Root.AllowDrop = true;
        _strip.Root.DragEnter += OnDragEnter;
        _strip.Root.DragOver += OnDragOver;
        _strip.Root.DragLeave += (_, _) =>
        {
            // DragLeave приходит и при переходе между иконками: сбрасываем, только если курсор ушёл с дока.
            if (GetCursorPos(out var c) && InHot(c)) return;
            _dragPaths = null;
            ClearExternalDrag();
        };
        _strip.Root.Drop += OnDrop;
        _s.WindowList.PointerLeft += () => { _listCloseTimer.Stop(); _listCloseTimer.Start(); };

        // Клик по доку не должен забирать фокус у окна, в котором работаешь (правило 0.6).
        _strip.Window.Activated += (_, e) => GuardFocus(e, _strip.Hwnd);
        _pill.Window.Activated += (_, e) => GuardFocus(e, _pill.Hwnd);

        _s.Tracker.Changed += Rebuild;
        _s.Tracker.ForegroundChanged += OnForeground;
        _s.Tracker.AttentionChanged += UpdateDots;
        _s.Buttons.Changed += UpdateDots;
        SubscribeTail();
        _s.Config.Changed += OnConfigChanged;
        _s.Launched += OnLaunched;
        Popups.CurrentChanged += OnPopupsChanged;
        _s.DropZones.Add(this);
    }

    void GuardFocus(WindowActivatedEventArgs e, IntPtr hwnd)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated) return;
        if (GetForegroundWindow() == hwnd) _s.Tracker.RestoreForeground();
    }

    void OnForeground()
    {
        EndKeyboard();
        RaiseAll();
        UpdateDots();
    }

    void OnConfigChanged()
    {
        if (UpdateTail()) Relayout();
        Rebuild();
    }

    void OnPopupsChanged()
    {
        // Окошко дока закрылось, курсор не над доком: таблетка снова ловит мышь только на себе.
        if (!_hover && !Popups.OwnedBy(this)) SetRegion(pill: true);
    }

    // ---------- IBar ----------

    public Screen Screen => _screen;
    public bool AtTop => false;
    public bool Pinned => _s.Config.Bars.DockPinned;
    public (int Left, int Right) EdgeSpan => (_screen.Bounds.Left, _screen.Bounds.Right);
    public IntPtr MainHwnd => _pill.Hwnd;
    /// Windows сам ставит наш резерв над своей панелью задач, поэтому её высоту не считаем.
    public int Reserve => Px(PillH + Bottom + 4);
    public bool Busy => _drag != null || _gapIndex >= 0 || _kb >= 0 || Popups.OwnedBy(this);

    /// Таблетка дока там, где она стоит, когда док выехал (физические пиксели экрана): над её
    /// левым краем открывается Пуск.
    public Rect PillPx => new(PillScreenX(), _stripY + Px(Headroom), Px(PillWidth()), Px(PillH));

    /// Кнопка Пуска, когда док выехал: над ней открывается меню Win+X.
    public Rect StartButtonPx
    {
        get
        {
            var x = _items.FirstOrDefault(i => i.IsStart)?.DX ?? PillLeft + Pad;
            return new Rect(_stripX + Px(x), _stripY + Px(Headroom + TopPad), Px(Icon), Px(Icon));
        }
    }

    // ---------- клавиатура: Win+1…9, Win+T, Win+B (раздел 6) ----------

    int _kb = -1;

    /// Win+цифра: как клик по n-й иконке (Shift — новое окно, Ctrl+Shift — от администратора).
    public void ClickIndex(int n, bool newWindow, bool admin)
    {
        var apps = _items.Where(i => !i.IsStart && !i.Removing && i.Entry != null).ToList();
        if (n < 1 || n > apps.Count) return;
        var it = apps[n - 1];
        _s.Bars.Reveal(this, holdMs: 800, force: true);
        if (admin) Launch(it, admin: true);
        else if (newWindow) Launch(it);
        else LeftClick(it);
    }

    List<object> KeyboardTargets()
    {
        var list = _items.Where(i => !i.Removing).Cast<object>().ToList();
        list.AddRange(_slots.Where(t => t.Shown));
        return list;
    }

    /// Win+T (с первой программы) или Win+B (с хвоста): стрелки ходят по доку, Enter — клик,
    /// клавиша меню или Shift+F10 — правый клик, Esc — выйти.
    public void BeginKeyboard(bool tail)
    {
        var targets = KeyboardTargets();
        _kb = tail ? Math.Max(0, targets.FindIndex(t => t is not Item)) : Math.Min(1, targets.Count - 1);
        _s.Keys.Capturing = true;
        _s.Bars.Reveal(this, force: true);
        ShowKeyboard();
    }

    public bool KeyboardActive => _kb >= 0;

    public void KeyboardKey(int vk, bool shift)
    {
        if (_kb < 0) return;
        var targets = KeyboardTargets();
        if (targets.Count == 0) { EndKeyboard(); return; }
        _kb = Math.Clamp(_kb, 0, targets.Count - 1);
        switch (vk)
        {
            case 0x25: _kb = Math.Max(0, _kb - 1); break;
            case 0x27: _kb = Math.Min(targets.Count - 1, _kb + 1); break;
            case 0x24: _kb = 0; break;
            case 0x23: _kb = targets.Count - 1; break;
            case 0x09: _kb = (_kb + (shift ? -1 : 1) + targets.Count) % targets.Count; break;
            case 0x0D or 0x20:
            {
                var t = targets[_kb];
                EndKeyboard();
                LeftClick(t);
                return;
            }
            case 0x5D or 0x79:
            {
                // Меню само принимает клавиатуру.
                var t = targets[_kb];
                EndKeyboard();
                RightClick(t);
                return;
            }
            case 0x1B: EndKeyboard(); return;
        }
        ShowKeyboard();
    }

    void ShowKeyboard()
    {
        var targets = KeyboardTargets();
        if (_kb < 0 || _kb >= targets.Count) return;
        var t = targets[_kb];
        HighlightSlot(t as Slot);
        if (t is Item it)
        {
            // Выделенная иконка увеличена, как под курсором.
            _cursorX = it.DX + it.Base / 2;
            SetHover(true);
            Kick();
        }
        else SetHover(false);
        HideTooltip();
        ShowTooltip(t, TooltipText(t));
    }

    public void EndKeyboard()
    {
        if (_kb < 0) return;
        _kb = -1;
        _s.Keys.Capturing = false;
        HideTooltip();
        HighlightSlot(null);
        SetHover(false);
    }

    void OnLaunched(string target)
    {
        foreach (var it in _items)
        {
            if (it.Entry is not { Running: false } e || !string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase)) continue;
            it.LaunchUntil = Environment.TickCount64 + 10_000;
            Kick();
        }
    }

    public RECT HotRect
    {
        get
        {
            var x = PillScreenX();
            var top = _stripY + Px(Headroom - Icon * (MaxScale - 1)) - 2;
            return new RECT { Left = x - Px(4), Top = top, Right = x + Px(PillWidth()) + Px(4), Bottom = _screen.Bounds.Bottom };
        }
    }

    public void SetReveal(double progress)
    {
        _reveal = progress;
        var hidden = _screen.Bounds.Bottom - (_stripY + Px(Headroom)) + Px(4);
        _slide = (int)Math.Round((1 - progress) * hidden);
        if (progress <= 0)
        {
            if (_windowsShown)
            {
                _pill.Hide();
                _strip.Hide();
                _windowsShown = false;
                SetHover(false);
            }
            return;
        }
        PlaceWindows();
        if (!_windowsShown)
        {
            _pill.Show(activate: false);
            _strip.Show(activate: false);
            _windowsShown = true;
            RaiseAll();
        }
    }

    // ---------- окно и место ----------

    /// Низ дока: над панелью задач Windows, пока она видна на этом мониторе (её скрытие — WSH-E9).
    int BaseBottom()
    {
        var bottom = _screen.Bounds.Bottom;
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var h = IntPtr.Zero;
            while ((h = FindWindowExW(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
            {
                if (!IsWindowVisible(h) || !GetWindowRect(h, out var r)) continue;
                var cx = (r.Left + r.Right) / 2;
                // Панель внизу этого монитора и не спрятана автоскрытием.
                if (cx >= _screen.Bounds.Left && cx < _screen.Bounds.Right && r.Bottom >= _screen.Bounds.Bottom - 2 && r.Top > _screen.Bounds.Top + _screen.Bounds.Height / 2
                    && r.Top < _screen.Bounds.Bottom - 4)
                    bottom = Math.Min(bottom, r.Top);
            }
        }
        return bottom;
    }

    public void Show()
    {
        UpdateTail();
        Relayout();
        Rebuild(initial: true);
        UpdateClock();
        Frame(0);
        _s.Bars.Add(this);
    }

    /// Монитор поменялся (размер, масштаб, панель задач) или он стал/перестал быть монитором хвоста.
    public bool FullTail => _fullTail;

    public void Move(Screen screen, bool fullTail, bool force = false)
    {
        // Ничего не поменялось (конфиг сохранили по другому поводу) — окна не трогаем.
        if (!force && screen.SameAs(_screen) && screen.Work.Bottom == _screen.Work.Bottom && fullTail == _fullTail && BaseBottom() - _stripH == _stripY) return;
        var tailChanged = fullTail != _fullTail;
        _screen = screen;
        _fullTail = fullTail;
        UpdateTail();
        Relayout();
        if (tailChanged) Kick();
        _s.Bars.Publish();
    }

    void Relayout()
    {
        var oldScale = _scale;
        _scale = _screen.Scale;
        _stripW = _screen.Bounds.Width;
        _stripH = (int)Math.Ceiling((Headroom + PillH + Bottom) * _scale);
        _stripX = _screen.Bounds.Left;
        _stripY = BaseBottom() - _stripH;
        _widthDip = _stripW / _scale;
        // Иконки нужны в пикселях нового масштаба.
        if (Math.Abs(oldScale - _scale) > 0.001) foreach (var it in _items) { var src = it.IconSource; it.IconSource = null; if (src != null && it.Entry != null) SetIcon(it, src, null); }
        _pillPx = (-1, -1, -1);
        Frame(0);
        PlaceWindows();
    }

    void PlaceWindows()
    {
        if (_reveal <= 0) return;
        // Порядок важен: каждый Place поднимает окно наверх, поэтому подложку ставим первой,
        // а окно с иконками вторым, иначе стекло закрывает иконки и ловит клики.
        _pillPx = (PillScreenX(), _stripY + _slide + Px(Headroom), Px(PillWidth()));
        _pill.Place(_pillPx.X, _pillPx.Y, _pillPx.W, Px(PillH));
        _strip.Place(_stripX, _stripY + _slide, _stripW, _stripH);
        Popups.Current?.Raise();
        if (!_hover) SetRegion(pill: true);
    }

    public void Close()
    {
        _s.Bars.Remove(this);
        _s.DropZones.Remove(this);
        _s.Tracker.Changed -= Rebuild;
        _s.Tracker.ForegroundChanged -= OnForeground;
        _s.Tracker.AttentionChanged -= UpdateDots;
        _s.Buttons.Changed -= UpdateDots;
        UnsubscribeTail();
        _s.Config.Changed -= OnConfigChanged;
        _s.Launched -= OnLaunched;
        EndKeyboard();
        Popups.CurrentChanged -= OnPopupsChanged;
        if (Popups.OwnedBy(this)) Popups.CloseAll();
        CompositionTarget.Rendering -= OnRendering;
        _clockTimer.Stop();
        _pill.Window.Close();
        _strip.Window.Close();
    }

    int Px(double dip) => (int)Math.Round(dip * _scale);

    /// Подложка ниже иконок, окошки выше дока (правило 0.10).
    void RaiseAll()
    {
        if (!_windowsShown) return;
        _pill.Raise();
        _strip.Raise();
        Popups.Current?.Raise();
    }

    // ---------- модель ----------

    void Rebuild() => Rebuild(initial: false);

    void Rebuild(bool initial)
    {
        // Пока тащат, док не пересобирается: пересоберётся, когда отпустят.
        if (_drag != null) return;
        var entries = DockModel.Build(_s.Config.Pins, _s.Tracker.Windows);
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
                if (!e.Pinned) Log.Write($"док: запущена {e.Title} ({e.Id}, окон {e.Windows.Count})");
            }
            var started = e.Running && item.Entry is { Running: false };
            item.Entry = e;
            if (e.Running) item.LaunchUntil = 0;
            if (item.IconWeak && (started || Environment.TickCount64 - item.IconAt > 20_000)) item.IconSource = null;
            SetIcon(item, IconSource(e), e.Windows.FirstOrDefault()?.Exe ?? Launcher.Resolve(e.Target).Exe);
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
        // Порядок поменялся (закрепили, убрали): соседи переезжают плавно.
        if (!initial && !old.Where(o => !o.Removing).Select(o => o.Key).SequenceEqual(next.Where(o => !o.Removing && old.Contains(o)).Select(o => o.Key)))
            _reorderUntil = Environment.TickCount64 + 500;
        _items.Clear();
        _items.AddRange(next);
        UpdateDots();
        Kick();
        // Открытый список окон показывает живой список.
        var list = _s.WindowList;
        if (list.IsOpen && ReferenceEquals(list.Owner, this) && _items.FirstOrDefault(i => Equals((this, i.Key), list.Anchor)) is { Entry: { } le } li && le.Windows.Count != list.Windows.Count)
        {
            if (le.Windows.Count >= 2) ShowWindowList(li);
            else list.Close(restoreFocus: false);
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
            Glyph = "",
            FontFamily = Theme.Icons,
            FontSize = ElementSize * 0.6,
            Foreground = Theme.Brush(Theme.TextSecondary),
            Visibility = Visibility.Collapsed,
        };
        // Счётчик — в правом верхнем углу, полоска загрузки — внизу иконки. Они внутри иконки,
        // поэтому растут вместе с ней под курсором.
        var badge = new Image
        {
            Width = ElementSize * 0.42, Height = ElementSize * 0.42, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        var fill = new Border { CornerRadius = new CornerRadius(ElementSize * 0.04), HorizontalAlignment = HorizontalAlignment.Left };
        var progress = new Grid
        {
            Height = ElementSize * 0.08,
            Margin = new Thickness(ElementSize * 0.12, 0, ElementSize * 0.12, ElementSize * 0.02),
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(ElementSize * 0.04),
            Background = Theme.Brush(Windows.UI.Color.FromArgb(0xB0, 0x20, 0x20, 0x20)),
            Children = { fill },
            Visibility = Visibility.Collapsed,
        };
        var visual = new Grid { Width = ElementSize, Height = ElementSize, Children = { placeholder, image, progress, badge } };
        var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = Theme.Brush(Theme.Text), Opacity = 0 };
        _canvas.Children.Insert(0, dot);
        _canvas.Children.Insert(0, visual);
        return new Item { Key = key, Visual = visual, Image = image, Dot = dot, Placeholder = placeholder, Badge = badge, Progress = progress, ProgressFill = fill };
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

    /// Иконка по AUMID или пути; если Проводник её не знает (свой AUMID у Electron-программ) или
    /// отдал заглушку, берём иконку exe, потом иконку окна. Пока своей нет, старая картинка
    /// остаётся на месте (лучше прежняя иконка, чем мигание значком).
    void SetIcon(Item item, string source, string? fallback)
    {
        if (item.IconSource == source || item.Image is null) return;
        item.IconSource = source;
        item.IconAt = Environment.TickCount64;
        var img = item.Image;
        void Apply(ImageSource? bmp, bool weak)
        {
            if (item.IconSource != source) return;
            item.IconWeak = weak;
            if (bmp is null && img.Source != null) return;
            img.Source = bmp;
            if (item.Placeholder != null) item.Placeholder.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
        }
        fallback ??= item.Entry?.Windows.FirstOrDefault()?.Exe ?? (item.Entry is { } en ? Launcher.Resolve(en.Target).Exe : null);
        var chain = new List<string> { source };
        if (fallback != null && !string.Equals(fallback, source, StringComparison.OrdinalIgnoreCase)) chain.Add(fallback);
        if (item.Entry?.Windows.FirstOrDefault() is { } w) chain.Add(Icons.WindowSource(w.Hwnd));
        // Иконка в размере увеличенной (1,6×): так она чёткая и в покое, и под курсором.
        var px = (int)Math.Ceiling(ElementSize * _scale);
        void Next(int i)
        {
            _s.Icons.Get(chain[i], px, bmp =>
            {
                if (item.IconSource != source) return;
                if (bmp != null) { Apply(bmp, weak: i > 0); return; }
                if (i + 1 < chain.Count) { Next(i + 1); return; }
                Log.Write($"док: нет картинки для {string.Join(", ", chain)}");
                Apply(null, weak: true);
            });
        }
        Next(0);
    }

    bool IsActive(DockEntry e) => e.Windows.Any(w => w.Hwnd == _s.Tracker.Foreground);

    void UpdateDots()
    {
        foreach (var it in _items)
        {
            if (it.IsStart || it.Entry is null) continue;
            var running = it.Entry.Running;
            var active = running && IsActive(it.Entry);
            var attention = running && it.Entry.Windows.Any(w => _s.Tracker.Attention.Contains(w.Hwnd));
            // Просит внимания: один раз подпрыгивает, точка оранжевая, пока не откроешь (1.1).
            if (attention && !it.Attention && AnimationsEnabled()) it.BounceV = -260;
            it.Attention = attention;
            it.Dot.Width = active ? 14 : 5;
            it.Dot.Height = active ? 4 : 5;
            it.Dot.CornerRadius = new CornerRadius(active ? 2 : 2.5);
            it.Dot.Background = Theme.Brush(attention ? Theme.Warning : Theme.Text);
            it.Dot.Opacity = !running ? 0 : active || attention ? 1 : 0.55;
            UpdateButtons(it);
        }
        Kick();
    }

    /// Счётчик и полоска загрузки, как их задала программа (раздел 8).
    void UpdateButtons(Item it)
    {
        if (it.Badge is null || it.Progress is null || it.ProgressFill is null || it.Entry is null) return;
        var windows = it.Entry.Windows.Select(w => w.Hwnd).ToList();
        var overlay = windows.Count > 0 ? _s.Buttons.OverlayOf(windows) : null;
        it.Badge.Source = overlay;
        it.Badge.Visibility = overlay is null ? Visibility.Collapsed : Visibility.Visible;
        var p = windows.Count > 0 ? _s.Buttons.ProgressOf(windows) : null;
        if (p is null) { it.Progress.Visibility = Visibility.Collapsed; return; }
        it.Progress.Visibility = Visibility.Visible;
        var color = p.State switch
        {
            Tray.TaskbarButtons.Error => Windows.UI.Color.FromArgb(255, 0xE8, 0x48, 0x48),
            Tray.TaskbarButtons.Paused => Windows.UI.Color.FromArgb(255, 0xE8, 0xB4, 0x2A),
            _ => Windows.UI.Color.FromArgb(255, 0x3C, 0xC8, 0x5A),
        };
        it.ProgressFill.Background = Theme.Brush(color);
        var track = ElementSize * 0.76;
        // Бегущая полоска (время неизвестно): заполнена целиком и полупрозрачная.
        var indeterminate = p.State == Tray.TaskbarButtons.Indeterminate;
        it.ProgressFill.Width = indeterminate ? track : Math.Max(track * Math.Clamp(p.Value, 0, 1), ElementSize * 0.08);
        it.ProgressFill.Opacity = indeterminate ? 0.6 : 1;
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
        // Esc во время перетаскивания: иконка возвращается на место (1.5).
        if (_drag != null && (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) EndDrag(commit: false);
        else if (_drag != null && CursorOnCanvas(_dragPoint) is var cp && cp != _dragPoint) UpdateDrag(cp);
        if (!Frame(dt))
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
            if (!_hover && !Popups.OwnedBy(this)) SetRegion(pill: true);
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

    double TailW() => SepW + _slots.Where(t => t.Shown).Sum(t => t.W + TailGap) - TailGap;

    double BaseTotal() => Pad + _items.Sum(i => i.Base + Gap * i.Base / Icon) - (_items.Count > 0 ? Gap : 0) + _gapW + TailW() + Pad;

    double PillLeft;
    double PillWidth() => BaseTotal() + _items.Sum(i => i == _drag ? 0 : i.Size - i.Base);
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
            if (it.Bounce != 0 || it.BounceV != 0) moving |= Spring(ref it.Bounce, ref it.BounceV, 0, dt, 120);
        }
        foreach (var gone in _items.Where(i => i.Removing && i.Base <= 0.01).ToList())
        {
            _items.Remove(gone);
            _canvas.Children.Remove(gone.Visual);
            _canvas.Children.Remove(gone.Dot);
        }
        // Промежуток для того, что тащат снаружи.
        var gapTarget = _gapIndex >= 0 ? Icon + Gap : 0;
        if (!animate) { _gapW = gapTarget; _gapV = 0; }
        else moving |= Spring(ref _gapW, ref _gapV, gapTarget, dt, 700);

        // Базовая раскладка (без увеличения) по центру полосы.
        var baseTotal = BaseTotal();
        var baseLeft = (_widthDip - baseTotal) / 2;
        var bl = new double[_items.Count];
        var x = baseLeft + Pad;
        for (var i = 0; i < _items.Count; i++)
        {
            if (i == _gapIndex) x += _gapW;
            bl[i] = x;
            x += _items[i].Base + Gap * _items[i].Base / Icon;
        }

        // Цели увеличения: волна от курсора по базовым центрам. Пока тащат, док не увеличивается.
        var magnify = _hover && _drag is null && _gapIndex < 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var center = bl[i] + it.Base / 2;
            var target = it.Base * (magnify ? Magnify(Math.Abs(_cursorX - center) / (Icon + Gap)) : 1);
            if (!animate) { it.Size = target; it.SizeV = 0; }
            else moving |= Spring(ref it.Size, ref it.SizeV, target, dt, 700);
        }

        // Точка под курсором стоит на месте: таблетка растёт влево и вправо по-разному, как у Mac.
        double extraLeft = 0, extraAll = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            if (it == _drag) continue;
            var e = it.Size - it.Base;
            extraAll += e;
            var frac = it.Base <= 0 ? (_cursorX > bl[i] ? 1 : 0) : Math.Clamp((_cursorX - bl[i]) / it.Base, 0, 1);
            extraLeft += e * frac;
        }
        var pillW = baseTotal + extraAll;
        PillLeft = Math.Clamp(baseLeft - extraLeft, 4, Math.Max(4, _widthDip - pillW - 4));

        // Иконки снизу по одной линии: растут вверх, низ стоит на месте.
        var iconBottom = Headroom + TopPad + Icon;
        var reordering = _drag != null || now < _reorderUntil;
        x = PillLeft + Pad;
        for (var i = 0; i < _items.Count; i++)
        {
            if (i == _gapIndex) x += _gapW;
            var it = _items[i];
            var size = it == _drag ? it.Base : it.Size;
            // Соседи переезжают на новое место пружиной (перестановка, закрепление).
            if (!it.Placed || !reordering || !animate) { it.DX = x; it.DXV = 0; it.Placed = true; }
            else moving |= Spring(ref it.DX, ref it.DXV, x, dt, 900);
            it.X = it.DX;
            it.Top = iconBottom - size + it.Bounce;
            if (it == _drag)
            {
                // Тащимая иконка едет за курсором.
                it.X = _dragPoint.X - _grabX;
                it.Top = Math.Max(0, _dragPoint.Y - _grabY);
            }
            var visualSize = it.IsStart ? Icon : ElementSize;
            var s = (float)(size / visualSize);
            it.Visual.Translation = new Vector3((float)it.X, (float)it.Top, 0);
            it.Visual.Scale = new Vector3(s, s, 1);
            var opacity = it.Entry?.Missing == true ? 0.4 : 1.0;
            if (it.LaunchUntil > now && it.Entry is { Running: false })
            {
                // Программа запускается: мягкая пульсация до появления окна, максимум 10 с.
                opacity = 0.6 + 0.4 * (0.5 + 0.5 * Math.Cos(now / 1000.0 * 2 * Math.PI / 1.1));
                moving = true;
            }
            if (it == _drag && _removeMode) opacity *= 0.6;
            it.Visual.Opacity = opacity;
            it.Dot.Opacity = it == _drag ? 0 : it.Dot.Opacity;
            it.Dot.Translation = new Vector3((float)(it.DX + size / 2 - it.Dot.Width / 2), (float)(Headroom + PillH - 7 - it.Dot.Height / 2), 0);
            x += size + Gap * it.Base / Icon;
        }
        x -= Gap;

        if (_drag != null && _removeMode)
        {
            _removeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var lw = _removeLabel.DesiredSize.Width;
            _removeLabel.Translation = new Vector3((float)(_drag.X + _drag.Base / 2 - lw / 2), (float)(_drag.Top + _drag.Base + 4), 0);
        }

        // Хвост: разделитель, ⌃ трей, сеть, звук, батарея, язык, уведомления, часы.
        var tailY = Headroom + PillH / 2;
        _separator.Translation = new Vector3((float)(x + 10), (float)(tailY - 17), 0);
        x += SepW;
        foreach (var t in _slots)
        {
            if (!t.Shown) continue;
            t.X = x;
            t.Visual.Translation = new Vector3((float)x, (float)(tailY - 20), 0);
            x += t.W + TailGap;
        }

        // Подсказка едет за своей иконкой.
        if (_tooltip.Visibility == Visibility.Visible) PlaceTooltip();

        // Подложка со стеклом повторяет таблетку.
        var px = (PillScreenX(), _stripY + _slide + Px(Headroom), Px(pillW));
        if (px != _pillPx && _windowsShown)
        {
            _pillPx = px;
            _pill.Move(px.Item1, px.Item2, px.Item3, Px(PillH));
            _s.Bars.Publish();
        }
        return moving || (_drag != null);
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
            SetWindowRgn(_strip.Hwnd, IntPtr.Zero, true);
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

    void LeaveIfOutside()
    {
        if (_drag != null || !_hover) { _hoverCheck.Stop(); return; }
        if (GetCursorPos(out var c) && InHot(c))
        {
            // Курсор ещё над доком: проверим снова, вдруг он уйдёт, не задев полосу.
            if (!_hoverCheck.IsRunning) _hoverCheck.Start();
            return;
        }
        _hoverCheck.Stop();
        SetHover(false);
    }

    /// Где курсор в координатах полосы. Положение из события указателя при перетаскивании
    /// иногда приходит не то (иконка улетала влево), а курсор знает точно.
    Point CursorOnCanvas(Point fallback) => GetCursorPos(out var c) ? ToCanvas(c.X, c.Y) : fallback;

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        if (_kb >= 0) EndKeyboard();
        if (_drag != null) { UpdateDrag(CursorOnCanvas(p)); return; }
        if (_dragCandidate != null && e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed
            && Math.Abs(p.X - _pressPoint.X) + Math.Abs(p.Y - _pressPoint.Y) > 6)
        {
            BeginDrag(_dragCandidate, CursorOnCanvas(p));
            return;
        }
        var pillW = PillWidth();
        // Пока курсор над доком, край считается чуть шире: таблетка растёт и сжимается вслед за
        // курсором, и без запаса у её краёв увеличение мигало бы «включено — выключено».
        var slack = _hover ? 8 : 0;
        var inside = p.X >= PillLeft - slack && p.X <= PillLeft + pillW + slack && p.Y >= Headroom - Icon * (MaxScale - 1) - 2 - slack;
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
        foreach (var t in _slots)
            if (t.Shown && p.X >= t.X && p.X < t.X + t.W && p.Y >= Headroom) return t;
        return null;
    }

    void SetHovered(object? target)
    {
        if (Equals(_hovered, target)) return;
        var hadTooltip = _tooltip.Visibility == Visibility.Visible && !Equals(_tooltipFor, "volume");
        _hovered = target;
        _hoverTimer.Stop();
        HighlightSlot(target as Slot);
        if (!Equals(_tooltipFor, "volume")) HideTooltip();
        var list = _s.WindowList;
        var listOpen = list.IsOpen && ReferenceEquals(list.Owner, this);
        if (target is null)
        {
            if (listOpen) { _listCloseTimer.Stop(); _listCloseTimer.Start(); }
            return;
        }
        // Открытый список окон сразу перескакивает на соседнюю иконку.
        if (target is Item { Entry.Windows.Count: >= 2 } it && listOpen)
        {
            _listCloseTimer.Stop();
            if (!Equals(list.Anchor, (this, it.Key))) ShowWindowList(it);
            return;
        }
        if (listOpen) { _listCloseTimer.Stop(); _listCloseTimer.Start(); }
        // Если подсказка уже была видна, соседняя появляется сразу (правило 0.9).
        var recent = hadTooltip || Environment.TickCount64 - _tooltipHiddenAt < 150;
        var wantsList = target is Item { Entry.Windows.Count: >= 2 };
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(wantsList ? 500 : recent ? 1 : 400);
        _hoverTimer.Start();
    }

    void OnHoverTimer()
    {
        if (_hovered is null || _pressed != null || _drag != null) return;
        if (Popups.Current is { IsOpen: true } p && p != _s.WindowList) return;
        if (_hovered is Item { Entry.Windows.Count: >= 2 } it) { ShowWindowList(it); return; }
        ShowTooltip(_hovered, TooltipText(_hovered));
    }

    string TooltipText(object target) => target switch
    {
        Item { IsStart: true } => "Пуск",
        Item { Entry: { Missing: true } } => "Не найдено",
        Item { Entry: { Windows.Count: 1 } e } => e.Windows[0].Title,
        Item { Entry: { } e } => e.Title,
        Slot t => SlotTooltip(t),
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
            case "volume": cx = _soundSlot.X + _soundSlot.W / 2; top = Headroom; break;
            case Slot t: cx = t.X + t.W / 2; top = Headroom; break;
            default: cx = _clockSlot.X + _clockSlot.W / 2; top = Headroom; break;
        }
        var x = Math.Clamp(cx - size.Width / 2, 2, _widthDip - size.Width - 2);
        _tooltip.Translation = new Vector3((float)x, (float)Math.Max(0, top - 8 - size.Height), 0);
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
        var y = _stripY + _slide;
        switch (target)
        {
            // Над увеличенной иконкой, как у Mac.
            case Item it: return new Rect(_stripX + it.X * _scale, y + it.Top * _scale, it.Size * _scale, (Headroom + PillH - it.Top) * _scale);
            case Slot t: x = t.X; w = t.W; break;
            default: x = _clockSlot.X; w = _clockSlot.W; break;
        }
        return new Rect(_stripX + x * _scale, y + Headroom * _scale, w * _scale, PillH * _scale);
    }

    void ShowWindowList(Item it)
    {
        if (it.Entry is null) return;
        HideTooltip();
        _listCloseTimer.Stop();
        _s.WindowList.Show((this, it.Key), AnchorPx(it), it.Entry.Windows, it.IconSource ?? "", this);
    }

    void CloseWindowList()
    {
        if (_s.WindowList.IsOpen && ReferenceEquals(_s.WindowList.Owner, this)) _s.WindowList.Close(restoreFocus: false);
    }

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        _pressed = HitTest(p.Position);
        _hoverTimer.Stop();
        if (!Equals(_tooltipFor, "volume")) HideTooltip();
        e.Handled = true;
        // Иконку можно потащить (кнопку Пуска и хвост нельзя).
        if (p.Properties.IsLeftButtonPressed && _pressed is Item { IsStart: false, Removing: false } it)
        {
            _dragCandidate = it;
            _pressPoint = p.Position;
            _strip.Root.CapturePointer(e.Pointer);
        }
    }

    void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas);
        _dragCandidate = null;
        if (_drag != null)
        {
            e.Handled = true;
            _dragPoint = CursorOnCanvas(p.Position);
            EndDrag(commit: true);
            _strip.Root.ReleasePointerCaptures();
            _pressed = null;
            return;
        }
        _strip.Root.ReleasePointerCaptures();
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
        if (!ReferenceEquals(HitTest(p.Position), _soundSlot)) return;
        var delta = p.Properties.MouseWheelDelta;
        if (delta == 0) return;
        var step = _s.AudioSettings.Step;
        _s.Audio.Volume = _s.Audio.Volume + Math.Sign(delta) * step;
        _hoverTimer.Stop();
        ShowTooltip("volume", $"{_s.Audio.Volume} %");
        _volumeTipTimer.Stop();
        _volumeTipTimer.Start();
    }

    // ---------- перетаскивание внутри дока (1.5) ----------

    int PinnedCount() => _items.Count(i => !i.Removing && i != _drag && i.Entry is { Pinned: true });

    void BeginDrag(Item it, Point p)
    {
        _drag = it;
        _dragCandidate = null;
        _dragFrom = _items.IndexOf(it);
        _grabX = _pressPoint.X - it.X;
        _grabY = _pressPoint.Y - it.Top;
        _dragPoint = p;
        _removeMode = false;
        HideTooltip();
        CloseWindowList();
        Popups.CloseAll();
        SetHover(true);
        UpdateDrag(p);
    }

    void UpdateDrag(Point p)
    {
        if (_drag is null) return;
        _dragPoint = p;
        // Вверх от дока больше чем на 80 px — «Убрать» (только закреплённое).
        _removeMode = _drag.Entry is { Pinned: true } && p.Y < Headroom + TopPad - RemoveDistance + _grabY;
        _removeLabel.Visibility = _removeMode ? Visibility.Visible : Visibility.Collapsed;
        // Куда встанет: по центрам остальных иконок.
        var others = _items.Where(i => i != _drag && !i.IsStart && !i.Removing).ToList();
        var center = p.X - _grabX + _drag.Base / 2;
        var slot = others.Count(o => o.X + o.Base / 2 < center);
        var pinned = PinnedCount();
        // Закреплённое переставляется среди закреплённых; запущенное можно утянуть к закреплённым.
        slot = _drag.Entry is { Pinned: true } ? Math.Clamp(slot, 0, pinned) : slot;
        others.Insert(Math.Clamp(slot, 0, others.Count), _drag);
        var order = new List<Item> { _items.First(i => i.IsStart) };
        order.AddRange(others);
        order.AddRange(_items.Where(i => i.Removing));
        if (!order.SequenceEqual(_items))
        {
            _items.Clear();
            _items.AddRange(order);
        }
        Kick();
    }

    void EndDrag(bool commit)
    {
        var it = _drag;
        if (it is null) return;
        _drag = null;
        _removeLabel.Visibility = Visibility.Collapsed;
        _reorderUntil = Environment.TickCount64 + 500;
        // Тащимая иконка возвращается в ряд пружиной с того места, где её отпустили.
        it.DX = _dragPoint.X - _grabX;
        it.DXV = 0;
        var remove = _removeMode;
        _removeMode = false;
        var index = _items.Where(i => !i.IsStart && !i.Removing).ToList().IndexOf(it);
        if (commit && it.Entry is { } e)
        {
            if (remove && e.Pinned)
            {
                _s.Unpin(e.Id);
                Rebuild();
                return;
            }
            var pinnedNow = _items.Where(i => !i.IsStart && !i.Removing && (i.Entry?.Pinned == true || i == it)).ToList();
            if (e.Pinned)
            {
                var ids = pinnedNow.Select(i => i.Entry!.Id).ToList();
                if (!ids.SequenceEqual(_s.Config.Pins.Select(p => p.Id)))
                {
                    _s.Config.Update(c => c.Dock.Pinned = ids.Select(id => c.Dock.Pinned.First(p => p.Id == id)).ToList());
                }
            }
            else if (index <= PinnedCount() && index != _dragFrom - 1)
            {
                // Запущенную незакреплённую утянули к закреплённым — закрепляется на этом месте.
                _s.PinApp(e.Title, e.Target, index);
            }
        }
        Rebuild();
    }

    // ---------- перетаскивание снаружи: Проводник и плитки категорий ----------

    /// Перевод экранных пикселей в координаты полосы.
    Point ToCanvas(int x, int y) => new((x - _stripX) / _scale, (y - _stripY - _slide) / _scale);

    bool OverDock(Point p) => _windowsShown && p.X >= PillLeft - 20 && p.X <= PillLeft + PillWidth() + 20 && p.Y >= Headroom - 30 && p.Y <= Headroom + PillH + Bottom;

    /// Индекс в `_items`, перед которым встанет брошенное (среди закреплённых).
    int GapIndexAt(double x)
    {
        var pinned = _items.Where(i => !i.IsStart && !i.Removing && i.Entry is { Pinned: true }).ToList();
        var before = pinned.Count(i => i.X + i.Size / 2 < x);
        if (before >= pinned.Count) return pinned.Count == 0 ? 1 : _items.IndexOf(pinned[^1]) + 1;
        return _items.IndexOf(pinned[before]);
    }

    int PinIndexOfGap() => _items.Take(Math.Max(0, _gapIndex)).Count(i => !i.IsStart && !i.Removing && i.Entry is { Pinned: true });

    Item? AppAt(double x) => _items.FirstOrDefault(i => !i.IsStart && !i.Removing && i.Entry is { Missing: false }
        && x >= i.X + i.Size * 0.2 && x <= i.X + i.Size * 0.8);

    void SetGap(int index)
    {
        if (index == _gapIndex) return;
        _gapIndex = index;
        Kick();
    }

    void ClearExternalDrag()
    {
        _springLoad.Stop();
        _dropOnto = null;
        if (_gapIndex >= 0) SetGap(-1);
        // Курсор не над доком: прозрачное место над таблеткой снова пропускает клики.
        if (_drag is null && !(GetCursorPos(out var c) && InHot(c))) SetHover(false);
    }

    bool InHot(POINT c)
    {
        var r = HotRect;
        return _windowsShown && c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom;
    }

    public bool DragOver(int sx, int sy, DragPayload payload)
    {
        var p = ToCanvas(sx, sy);
        if (!OverDock(p) || payload.Item.Kind != "app") { DragLeave(); return false; }
        if (!_hover) SetHover(true);
        SetGap(GapIndexAt(p.X));
        return true;
    }

    public void DragLeave() => ClearExternalDrag();

    public bool Drop(int sx, int sy, DragPayload payload)
    {
        var p = ToCanvas(sx, sy);
        var ok = OverDock(p) && payload.Item.Kind == "app";
        if (ok)
        {
            if (_gapIndex < 0) _gapIndex = GapIndexAt(p.X);
            _s.PinApp(payload.Item.Title, payload.Item.Target, PinIndexOfGap());
        }
        ClearExternalDrag();
        return ok;
    }

    async void OnDragEnter(object sender, DragEventArgs e)
    {
        // DragEnter приходит и от каждой иконки, над которой проходит курсор: файлы читаем один раз.
        if (_dragPaths != null) { OnDragOver(sender, e); return; }
        _dragPrograms = true;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            _dragPaths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            _dragPrograms = _dragPaths.Count > 0 && _dragPaths.All(ShellConfig.IsProgram);
            OnDragOver(sender, e);
        }
        catch { }
        finally { deferral.Complete(); }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.None; return; }
        var p = e.GetPosition(_canvas);
        if (!_hover) SetHover(true);
        // Над кнопкой Пуска: подержать — откроется Пуск с категориями.
        if (_items.FirstOrDefault(i => i.IsStart) is { } start && p.X >= start.X && p.X <= start.X + start.Size)
        {
            SetGap(-1);
            if (_dropOnto != start)
            {
                _dropOnto = start;
                _springLoad.Stop();
                _springLoad.Start();
            }
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }
        if (_dropOnto is { IsStart: true }) { _dropOnto = null; _springLoad.Stop(); }
        if (_dragPrograms)
        {
            // Программы и ярлыки: между иконками промежуток, отпустил — закрепилось там.
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Закрепить в доке";
            SetGap(GapIndexAt(p.X));
            return;
        }
        // Файл: можно бросить только на иконку программы — откроется в ней.
        SetGap(-1);
        var onto = AppAt(p.X);
        if (onto != _dropOnto)
        {
            _dropOnto = onto;
            _springLoad.Stop();
            if (onto?.Entry?.Running == true) _springLoad.Start();
        }
        if (onto is null) { e.AcceptedOperation = DataPackageOperation.None; return; }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"Открыть в «{onto.Entry!.Title}»";
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var paths = _dragPaths;
        var onto = _dropOnto;
        var pinAt = PinIndexOfGap();
        var programs = _dragPrograms;
        _dragPaths = null;
        ClearExternalDrag();
        if (paths is null || paths.Count == 0 || onto is { IsStart: true }) return;
        if (programs)
        {
            foreach (var path in paths)
            {
                var kept = ShellConfig.KeepShortcut(path);
                _s.PinApp(System.IO.Path.GetFileNameWithoutExtension(path), kept, pinAt++);
            }
            return;
        }
        if (onto?.Entry is { } entry)
        {
            var exe = entry.Windows.FirstOrDefault()?.Exe ?? Launcher.Resolve(entry.Target).Exe;
            if (exe != null) Launcher.OpenWith(exe, paths);
        }
    }

    // ---------- клики ----------

    void LeftClick(object target)
    {
        CloseWindowList();
        if (target is Slot slot) { SlotClick(slot); return; }
        if (target is not Item it) return;
        // Пуск: повторный клик закрывает его, клик по Пуску другого монитора переносит.
        if (it.IsStart) { _s.ToggleStart(this); return; }
        // Один клик = одно действие: открытое меню закрывается, и клик срабатывает.
        if (Popups.Current is { IsOpen: true }) Popups.CloseAll(restoreFocus: true);
        var entry = it.Entry!;
        var shift = KeyDown(VK_SHIFT);
        var ctrl = KeyDown(VK_CONTROL);
        if (entry.Missing)
        {
            // Программу удалили: предлагаем убрать её из дока.
            var key = ("missing", this, it.Key);
            _s.Menu.Show(key, AnchorPx(it),
            [
                new() { Text = "Программа не найдена", Glyph = "", Enabled = false },
                MenuItem.Sep,
                new() { Text = "Убрать из дока", Glyph = "", Invoke = () => _s.Unpin(entry.Id) },
            ], owner: this);
            return;
        }
        if (shift && ctrl) { Launch(it, admin: true); return; }
        if (shift) { Launch(it); return; }
        var recent = entry.Windows.FirstOrDefault();
        if (recent is null) { Launch(it); return; }
        if (recent.Hwnd == _s.Tracker.Foreground && !recent.Minimized) _s.Tracker.Minimize(recent.Hwnd);
        else _s.Tracker.Activate(recent.Hwnd);
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
        if (ReferenceEquals(target, _soundSlot)) { _s.Audio.Muted = !_s.Audio.Muted; return; }
        if (target is Item { IsStart: false, Entry.Missing: false } it) Launch(it);
    }

    void RightClick(object target)
    {
        CloseWindowList();
        HideTooltip();
        if (target is Slot slot) { SlotRightClick(slot); return; }
        if (target is not Item it) return;
        // Правый клик по Пуску — служебное меню, то же, что Win+X (3.7).
        if (it.IsStart) { _s.ToggleTools(this); return; }
        var key = ("menu", this, it.Key);
        // Правый клик по другой иконке при открытом меню: меню перескакивает одним кликом.
        Popups.Toggle(key, () => _s.Menu.Show(key, AnchorPx(it), AppMenu(it), owner: this));
    }

    List<MenuItem> SoundMenu()
    {
        var current = _s.Audio.DefaultId();
        return
        [
            new()
            {
                Text = "Устройства вывода",
                Glyph = "",
                Children = _s.Audio.Devices().Select(d => new MenuItem { Text = d.Name, Checked = d.Id == current, Invoke = () => _s.Audio.SetDefault(d.Id) }).ToList(),
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
                items.Add(new MenuItem { Text = w.Title, Checked = h == _s.Tracker.Foreground, Invoke = () => _s.Tracker.Activate(h) });
            }
            items.Add(MenuItem.Sep);
        }
        // Списки быстрых действий программы (раздел 8): недавние файлы и её действия.
        var exe = e.Windows.FirstOrDefault()?.Exe ?? Launcher.Resolve(e.Target).Exe;
        var aumid = e.Windows.FirstOrDefault()?.Aumid ?? (Launcher.IsPath(e.Target) ? null : e.Target);
        var recent = JumpLists.Recent(aumid);
        if (recent.Count > 0)
            items.Add(new MenuItem
            {
                Text = "Недавние",
                Glyph = "\uE81C",
                Children = recent.Select(r => new MenuItem { Text = r.Title, Invoke = () => Launcher.Open(r.Path) }).ToList(),
            });
        var tasks = JumpLists.Tasks(exe);
        foreach (var t in tasks) items.Add(new MenuItem { Text = t.Title, Invoke = () => Launcher.Run(t.Path, t.Args) });
        if (recent.Count > 0 || tasks.Count > 0) items.Add(MenuItem.Sep);
        items.Add(new MenuItem { Text = e.Running ? "Новое окно" : "Открыть", Glyph = e.Running ? "" : "", Enabled = !e.Missing, Invoke = () => Launch(it) });
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
            items.Add(new MenuItem { Text = "Убрать из дока", Glyph = "", Invoke = () => _s.Unpin(e.Id) });
        else
            items.Add(new MenuItem { Text = "Закрепить в доке", Glyph = "", Invoke = () => _s.PinApp(e.Title, e.Target) });
        var cats = _s.Config.Categories;
        items.Add(new MenuItem
        {
            Text = "Добавить в категорию",
            Glyph = "",
            Enabled = cats.Count > 0,
            Children = cats.Select(c => new MenuItem
            {
                Text = c.Name,
                Checked = c.Items.Any(i => string.Equals(i.Target, e.Target, StringComparison.OrdinalIgnoreCase)),
                Invoke = () => ToggleCategory(c.Id, e),
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
                Invoke = () => { foreach (var h in windows) _s.Tracker.Close(h); },
            });
        }
        return items;
    }

    void ToggleCategory(string categoryId, DockEntry e)
    {
        var cat = _s.Config.CategoryById(categoryId);
        if (cat is null) return;
        var existing = cat.Items.FirstOrDefault(i => string.Equals(i.Target, e.Target, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _s.Config.Update(_ => cat.Items.Remove(existing));
        else _s.AddToCategory(categoryId, [new LauncherItem { Kind = "app", Title = e.Title, Target = e.Target, Meta = "Приложение" }]);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);
}
