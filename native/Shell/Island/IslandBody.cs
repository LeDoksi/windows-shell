using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WindowsShell.Island.Core;
using WindowsShell.Island.Widgets;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Island.IslandUi;
using static WindowsShell.Native.Win32;
using Color = Windows.UI.Color;
using XPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WindowsShell.Island;

public enum BodyMode { Rest, Ribbon, Widget, Event, Badge, Drop }

/// Куда бросили файлы в режиме приёма (П2).
public enum DropTarget { None, Bin, Shelf }

/// Тело острова на одном мониторе (путь b1 из пробы E22): одно неподвижное прозрачное окно
/// с запасом; форма — путь по контуру IslandShape, клики — SetWindowRgn по тому же контуру,
/// в покое окно сквозное для мыши. Кромка → лента → виджет → пилюля события — одно тело,
/// меняющее форму на пружинах; содержимое не двигается, проявляется на последней трети пути.
public sealed class IslandBody
{
    const double BaseW = 680, BaseH = 640, MagnetPad = 60, RestW = 190, RestH = 3, HotY = 2, HotHalfW = 140, Pad = 18;
    const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;

    double WinW = BaseW, WinH = BaseH;
    readonly IslandHost _host;
    readonly Services _s;
    readonly ShellWindow _win;
    readonly XPath _shape;
    readonly Border _dot;
    readonly Grid _content;
    readonly RibbonView _ribbon;
    readonly ScrollViewer _scroll;
    readonly Border _widgetHost;
    readonly Border _pillHost;
    readonly Border _chip;
    readonly DropView _drop;
    readonly TextBlock _chipText;
    readonly Dictionary<string, IslandWidget> _widgets = [];
    readonly HoverGate _gate = new();
    readonly Magnet _magnet = new();
    readonly DispatcherQueueTimer _pillTimer, _chipTimer;

    Spring _w = new() { Value = RestW, Target = RestW };
    Spring _h = new() { Value = RestH, Target = RestH };
    SpringParams _p = Springs.Ribbon;
    double _fromW = RestW, _fromH = RestH, _targetW = RestW;
    bool _moving, _transparent, _hidden, _activated, _contentShown, _disposed;
    IntPtr _restoreTo;
    long _pillUntil, _raisedAt;
    Action? _pillClick, _chipClick;
    double _cursorX, _cursorY;
    bool _cursorOn;
    // Постоянная пилюля: идущая запись («rec») и закреплённый монитор ПК («pin», П8) — одной
    // строкой; тело возвращается в неё, а не в кромку.
    static readonly string[] BadgeOrder = ["rec", "pin"];
    readonly Dictionary<string, (Border Part, Action? Click)> _badges = [];
    readonly StackPanel _badgeRow = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    UIElement? _badge => _badges.Count > 0 ? _badgeRow : null;
    // Приём файлов (П2): кнопку зажали не у края, курсор пришёл к краю — окно ловит DragEnter.
    bool _probe, _wasAtHot;
    long _dropLeftAt, _dropReleasedAt;

    public Screen Screen { get; private set; }
    public BodyMode Mode { get; private set; } = BodyMode.Rest;
    public IslandWidget? Widget { get; private set; }
    public bool Full { get; private set; }
    public IntPtr Hwnd => _win.Hwnd;
    public bool Moving => _moving || _magnet.Moving;
    public bool Waiting => _gate.Waiting;

    /// Окно, в котором работали до острова (туда — смена раскладки, туда возвращается фокус).
    public IntPtr WorkWindow
    {
        get
        {
            if (_restoreTo != IntPtr.Zero && IsWindow(_restoreTo)) return _restoreTo;
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && ProcessId(fg) != (uint)Environment.ProcessId) return fg;
            return _s.Tracker.Foreground;
        }
    }

    public IslandBody(IslandHost host, Services s, Screen screen)
    {
        _host = host;
        _s = s;
        Screen = screen;
        _win = new ShellWindow(Surface.Transparent, noActivate: true, rounded: false, transitions: false);
        // Имя окна: его читает экранный диктор и находит проверка --island-test.
        _win.Window.Title = SelfTest.WindowTitle;
        var canvas = new Canvas();
        _win.Root.Children.Add(canvas);
        _shape = new XPath { Fill = B(Body), Stroke = B(Outline), StrokeThickness = 1 };
        canvas.Children.Add(_shape);
        // П21: непрочитанное — акцентная точка на правом конце кромки.
        _dot = new Border { Width = 12, Height = 3, CornerRadius = new CornerRadius(0, 0, 1.5, 1.5), Background = B(Theme.Accent), Visibility = Visibility.Collapsed };
        Canvas.SetLeft(_dot, WinW / 2 + RestW / 2 - 14);
        canvas.Children.Add(_dot);

        _ribbon = new RibbonView(s, id => _host.SlotClicked(this, id));
        _widgetHost = new Border { Padding = new Thickness(Pad, 4, Pad, Pad) };
        _scroll = new ScrollViewer
        {
            Content = _widgetHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };
        _pillHost = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        _pillHost.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(_pillHost).Properties.IsLeftButtonPressed) return;
            if (Mode == BodyMode.Badge)
            {
                // В строке несколько частей (запись, монитор): у каждой своё действие.
                foreach (var (part, partClick) in _badges.Values)
                {
                    var pt = e.GetCurrentPoint(part).Position;
                    if (pt.X >= 0 && pt.Y >= 0 && pt.X <= part.ActualWidth && pt.Y <= part.ActualHeight) { partClick?.Invoke(); return; }
                }
                return;
            }
            var click = _pillClick;
            if (click != null) click();
            else Sleep();
        }), true);
        _chipText = Text("", 12, Ink);
        _chip = new Border
        {
            Child = _chipText,
            Background = B(Color.FromArgb(255, 0x1F, 0x3A, 0x4A)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 4, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
        };
        _chip.Tapped += (_, _) => _chipClick?.Invoke();
        var stack = new StackPanel { Children = { _ribbon.Panel, _scroll } };
        _drop = new DropView();
        _content = new Grid { IsHitTestVisible = false, Children = { stack, _pillHost, _chip, _drop.View } };
        canvas.Children.Add(_content);

        _pillTimer = s.Queue.CreateTimer();
        _pillTimer.Interval = TimeSpan.FromMilliseconds(100);
        _pillTimer.Tick += (_, _) => PillTick();
        _chipTimer = s.Queue.CreateTimer();
        _chipTimer.IsRepeating = false;
        _chipTimer.Tick += (_, _) => { _chip.Visibility = Visibility.Collapsed; _chipClick = null; };

        _win.Window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated || Mode != BodyMode.Widget || !_activated) return;
            // Ушли в другое окно (Alt+Tab, клик): закрываемся. Своё меню (правый клик по устройству)
            // и системный диалог, открытый из острова, остров не сворачивают (П5).
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || ProcessId(fg) == (uint)Environment.ProcessId || ClassName(fg) == "#32770") return;
            Sleep(restoreFocus: false);
        };
        _win.Root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && Mode != BodyMode.Rest) { e.Handled = true; Sleep(); }
        };

        // Приём файлов (П2, И9): цели — корзина и полка. Корень прозрачен для глаза, но ловит
        // перетаскивание; где окно «есть», решает регион (в покое — кромка).
        _win.Root.Background = B(Color.FromArgb(0, 0, 0, 0));
        _win.Root.AllowDrop = true;
        _win.Root.DragEnter += OnDragEnter;
        _win.Root.DragOver += OnDragOver;
        _win.Root.DragLeave += (_, _) => { _drop.Hover(DropTarget.None); _dropLeftAt = Environment.TickCount64; };
        _win.Root.Drop += OnDrop;

        Place();
        AddExStyle(_win.Hwnd, WS_EX_LAYERED);
        SetLayeredWindowAttributes(_win.Hwnd, 0, 255, 2 /* LWA_ALPHA */);
        // П9: остров не попадает в снимки и запись экрана.
        SetWindowDisplayAffinity(_win.Hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
        _win.Show(false);
        EnterRest();
        Draw();
    }

    RectI Monitor => new(Screen.Bounds.Left, Screen.Bounds.Top, Screen.Bounds.Right, Screen.Bounds.Bottom);
    int CenterPx => (int)(((long)Screen.Bounds.Left + Screen.Bounds.Right) / 2);

    void Place()
    {
        var r = IslandLayout.Place(Monitor, Screen.Scale, WinW, WinH);
        _win.Place(r.Left, r.Top, r.Width, r.Height);
    }

    /// Монитор поменял место или масштаб (в том числе во время анимации).
    public void Move(Screen s)
    {
        var same = s.SameAs(Screen);
        Screen = s;
        if (same) return;
        Place();
        Draw();
        _host.PublishZones();
    }

    // ---------- состояния ----------

    /// Лента по наведению (П1).
    public void Wake()
    {
        if (Full || _disposed) return;
        _ribbon.Build(_s.Config.Island.Slots, null);
        _ribbon.Select(null);
        _host.Opening(this);
        SetMode(BodyMode.Ribbon, _ribbon.Width, RibbonView.Height);
    }

    /// Свернуть в кромку. restoreFocus — вернуть фокус окну, где работали до острова (П5).
    public void Sleep(bool restoreFocus = true)
    {
        if (_disposed) return;
        if (Mode == BodyMode.Rest || (Mode == BodyMode.Badge && _badge != null)) return;
        CloseWidget();
        _pillTimer.Stop();
        _pillClick = null;
        _chip.Visibility = Visibility.Collapsed;
        _gate.Reset();
        Deactivate(restoreFocus);
        if (_badge != null) EnterBadge();
        else SetMode(BodyMode.Rest, RestW, RestH);
    }

    // ---------- постоянная пилюля (запись) ----------

    /// Содержимое постоянной пилюли в слоте ("rec" — запись, "pin" — монитор ПК), null — слота нет.
    public UIElement? BadgeContent(string slot) => _badges.TryGetValue(slot, out var b) ? b.Part.Child : null;

    public bool HasBadge(string slot) => _badges.ContainsKey(slot);

    /// Задать или убрать часть постоянной пилюли. Тот же элемент ещё раз — пересчитать размер (таймер вырос).
    public void SetBadge(string slot, UIElement? content, Action? click)
    {
        if (_disposed) return;
        var had = _badges.TryGetValue(slot, out var old);
        if (content == null)
        {
            if (!had) return;
            old.Part.Child = null;
            _badges.Remove(slot);
            ComposeBadges();
            if (_badges.Count == 0)
            {
                if (Mode == BodyMode.Badge) SetMode(BodyMode.Rest, RestW, RestH);
                return;
            }
        }
        else if (!had || old.Part.Child != content || old.Click != click)
        {
            var part = had ? old.Part : new Border { VerticalAlignment = VerticalAlignment.Center };
            if (part.Child != content) part.Child = content;
            _badges[slot] = (part, click);
            if (!had) ComposeBadges();
        }
        if (Mode == BodyMode.Rest) EnterBadge();
        else if (Mode == BodyMode.Badge)
        {
            if (_pillHost.Child != _badgeRow) _pillHost.Child = _badgeRow;
            var (w, h) = PillSize();
            if (Math.Abs(w - _w.Target) > 0.5 || Math.Abs(h - _h.Target) > 0.5) { _targetW = w; _content.Width = w; Canvas.SetLeft(_content, WinW / 2 - w / 2); Retarget(w, h, Springs.Ribbon); }
        }
    }

    /// Части строки по порядку, между ними тонкая черта.
    void ComposeBadges()
    {
        _badgeRow.Children.Clear();
        foreach (var slot in BadgeOrder)
        {
            if (!_badges.TryGetValue(slot, out var b)) continue;
            if (_badgeRow.Children.Count > 0)
                _badgeRow.Children.Add(new Border { Width = 1, Height = 18, Background = B(Track), VerticalAlignment = VerticalAlignment.Center });
            _badgeRow.Children.Add(b.Part);
        }
    }

    void EnterBadge()
    {
        if (_badge == null) return;
        _pillHost.Child = _badgeRow;
        _pillClick = null;
        if (_hidden) { _win.Show(false); _hidden = false; }
        _win.Raise();
        _raisedAt = Environment.TickCount64;
        _pillHost.Visibility = Visibility.Visible;
        var (w, h) = PillSize();
        SetMode(BodyMode.Badge, w, h);
        _pillTimer.Start();
    }

    (double W, double H) PillSize()
    {
        _pillHost.Measure(new Windows.Foundation.Size(WinW, 200));
        var size = _pillHost.DesiredSize;
        return (Math.Max(64, size.Width), Math.Max(30, size.Height));
    }

    /// Открыть виджет в теле. byUser — кликом (можно и поверх игры; берём фокус: Escape, Tab, ввод
    /// пароля). focus: false — без фокуса (клик по строке закреплённого монитора не выбивает игру, П8).
    public void OpenWidget(string id, bool byUser, bool focus = true)
    {
        if (_disposed) return;
        if (Full && !byUser) return;
        var w = _widgets.TryGetValue(id, out var existing) ? existing : _widgets[id] = IslandWidgets.Create(id, _s, this);
        _host.Opening(this);
        // Большое состояние (лист настроек): окно сначала вырастает, потом тело раскрывается в нём.
        if (w.Sheet(Screen) is { } sheet) EnsureSize(sheet.W + 2 * MagnetPad, sheet.H + MagnetPad);
        if (Widget != w)
        {
            CloseWidget();
            Widget = w;
            _widgetHost.Child = w.View;
            _contentShown = false;
            w.Open();
        }
        _ribbon.Build(_s.Config.Island.Slots, id);
        _ribbon.Select(id);
        _pillTimer.Stop();
        _pillClick = null;
        _gate.Reset(awake: true);
        if (byUser && focus) Activate();
        var (tw, th) = WidgetSize(w);
        SetMode(BodyMode.Widget, tw, th);
    }

    void CloseWidget()
    {
        if (Widget is null) return;
        Widget.Close();
        _widgetHost.Child = null;
        Widget = null;
    }

    /// Содержимое виджета поменяло размер.
    public void WidgetResized(IslandWidget w)
    {
        if (w != Widget || Mode != BodyMode.Widget) return;
        var (tw, th) = WidgetSize(w);
        if (Math.Abs(tw - _w.Target) < 0.5 && Math.Abs(th - _h.Target) < 0.5) return;
        _targetW = tw;
        _content.Width = tw;
        Canvas.SetLeft(_content, WinW / 2 - tw / 2);
        Retarget(tw, th, _contentShown ? Springs.Switch : _p);
    }

    (double W, double H) WidgetSize(IslandWidget w)
    {
        var width = Math.Max(_ribbon.Width, w.Width);
        var maxContent = (w.Sheet(Screen) is { } sheet ? Math.Min(WinH, sheet.H) : Math.Min(WinH - MagnetPad, BaseH - MagnetPad)) - RibbonView.Height;
        _scroll.Visibility = Visibility.Visible;
        _scroll.MaxHeight = maxContent;
        _scroll.Width = width;
        _widgetHost.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var h = Math.Min(maxContent, _widgetHost.DesiredSize.Height);
        return (width, RibbonView.Height + h);
    }

    /// Пилюля события из кромки (П7). Открыт виджет — маленький чип внизу тела, виджет не меняется.
    public void ShowPill(UIElement content, string chipText, int ms, Action? click)
    {
        if (_disposed) return;
        if (Mode == BodyMode.Widget)
        {
            _chipText.Text = chipText;
            _chipClick = click;
            _chip.Visibility = Visibility.Visible;
            _chipTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(ms, 3000));
            _chipTimer.Stop();
            _chipTimer.Start();
            return;
        }
        if (Mode is BodyMode.Ribbon or BodyMode.Drop) return; // лента сама показывает то же на значке
        _pillHost.Child = content;
        _pillClick = click;
        _pillUntil = Environment.TickCount64 + ms;
        if (_hidden) { _win.Show(false); _hidden = false; }
        _win.Raise();
        _raisedAt = Environment.TickCount64;
        _pillHost.Visibility = Visibility.Visible;
        var (pw, ph) = PillSize();
        SetMode(BodyMode.Event, pw, ph);
        _pillTimer.Start();
    }

    void PillTick()
    {
        if (Mode is not (BodyMode.Event or BodyMode.Badge)) { _pillTimer.Stop(); return; }
        var now = Environment.TickCount64;
        // Игра тоже бывает «поверх всех»: раз в секунду поднимаемся над ней.
        if (now - _raisedAt > 1000) { _win.Raise(); _raisedAt = now; }
        if (Mode == BodyMode.Badge) return;
        var hover = _cursorOn && _cursorY <= _h.Value + 4 && Math.Abs(_cursorX) <= _w.Value / 2 + 4;
        if (hover) _pillUntil = Math.Max(_pillUntil, now + 600);
        if (now >= _pillUntil) Sleep(restoreFocus: false);
    }

    void SetMode(BodyMode m, double w, double h)
    {
        var from = Mode;
        _p = m is BodyMode.Rest or BodyMode.Ribbon or BodyMode.Event or BodyMode.Badge ? Springs.Ribbon
            : from == BodyMode.Widget ? Springs.Switch : Springs.Widget;
        Mode = m;
        _fromW = _w.Value;
        _fromH = _h.Value;
        _targetW = w;
        _content.Width = w;
        Canvas.SetLeft(_content, WinW / 2 - w / 2);
        _ribbon.Panel.Visibility = m is BodyMode.Ribbon or BodyMode.Widget ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = m == BodyMode.Widget ? Visibility.Visible : Visibility.Collapsed;
        if (m is not (BodyMode.Event or BodyMode.Badge)) { _pillHost.Visibility = Visibility.Collapsed; _pillHost.Child = null; }
        _drop.View.Visibility = m == BodyMode.Drop ? Visibility.Visible : Visibility.Collapsed;
        if (m != BodyMode.Drop) _drop.Hover(DropTarget.None);
        if (m != BodyMode.Widget) _chip.Visibility = Visibility.Collapsed;
        // Уходит быстрее, чем приходит: старое содержимое гаснет сразу.
        if (from != m || m == BodyMode.Widget) { _scroll.Opacity = 0; _pillHost.Opacity = 0; _drop.View.Opacity = 0; }
        if (m != BodyMode.Rest) LeaveRest();
        _content.IsHitTestVisible = m != BodyMode.Rest;
        Retarget(w, h, _p);
        _host.PublishZones();
    }

    void Retarget(double w, double h, SpringParams p)
    {
        _p = p;
        _w.Target = w;
        _h.Target = h;
        if (!AnimationsEnabled())
        {
            // Reduced motion («Эффекты анимации» выключены): без пружин и деформации.
            _w = new Spring { Value = w, Target = w };
            _h = new Spring { Value = h, Target = h };
        }
        _moving = true;
        _host.EnsureRendering();
    }

    void EnterRest()
    {
        if (_transparent) return;
        AddExStyle(_win.Hwnd, WS_EX_TRANSPARENT);
        _transparent = true;
        if (Full) Hide();
    }

    void LeaveRest()
    {
        if (_hidden) { _win.Show(false); _hidden = false; _win.Raise(); }
        if (!_transparent) return;
        AddExStyle(_win.Hwnd, 0, WS_EX_TRANSPARENT);
        _transparent = false;
    }

    void Hide()
    {
        if (_hidden) return;
        _win.Hide();
        _hidden = true;
    }

    void Activate()
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && ProcessId(fg) != (uint)Environment.ProcessId) _restoreTo = fg;
        AddExStyle(_win.Hwnd, 0, WS_EX_NOACTIVATE);
        ShellWindow.ForceForeground(_win.Hwnd);
        _activated = true;
        // Фокус на выбранный значок: работают Escape, Tab в виджет, стрелки по ленте.
        _s.Queue.TryEnqueue(DispatcherQueuePriority.Low, () => _ribbon.FocusSelected());
    }

    void Deactivate(bool restoreFocus)
    {
        if (!_activated) return;
        _activated = false;
        AddExStyle(_win.Hwnd, WS_EX_NOACTIVATE);
        if (restoreFocus && GetForegroundWindow() == _win.Hwnd && _restoreTo != IntPtr.Zero && IsWindow(_restoreTo))
            ShellWindow.ForceForeground(_restoreTo);
        _restoreTo = IntPtr.Zero;
    }

    /// Fullscreen на этом мониторе: кромки нет, наведение выключено; раскрытый остров сворачивается (П6).
    public void SetFull(bool full)
    {
        if (Full == full) return;
        Full = full;
        if (full)
        {
            if (Mode is BodyMode.Ribbon or BodyMode.Widget) Sleep(restoreFocus: false);
            else if (Mode == BodyMode.Rest && !_moving) Hide();
        }
        else if (_hidden)
        {
            _win.Show(false);
            _hidden = false;
            _win.Raise();
        }
        _host.PublishZones();
    }

    /// Проводник перезапустился или игра встала поверх: поднять окно.
    public void Raise() { if (!_hidden) _win.Raise(); }

    /// Индикаторы ленты и кромки (П20, П21).
    public void RefreshIndicators()
    {
        _ribbon.Refresh();
        Draw();
    }

    // ---------- курсор ----------

    /// Такт опроса курсора (поток интерфейса). true — нужен кадр (магнит).
    public bool Tick(POINT p, bool held, bool pressed, bool esc, long now)
    {
        var s = Screen.Scale;
        _cursorX = (p.X - CenterPx) / s;
        _cursorY = (p.Y - Screen.Bounds.Top) / s;
        _cursorOn = Screen.Contains(p.X, p.Y) && !_hidden;
        var cx = Math.Abs(_cursorX);
        var w = _w.Value;
        var h = _h.Value;
        var atHot = _cursorOn && _cursorY < HotY && cx < Math.Max(HotHalfW, w / 2);
        var inside = _cursorOn && _cursorY <= h + 8 && cx <= w / 2 + 8;

        // Приём файлов (П2): к краю пришли с зажатой кнопкой (не нажали у самого края —
        // это клик по вкладке под кромкой). Окно ловит DragEnter; тащат окно — его не будет.
        var arrived = atHot && !_wasAtHot;
        _wasAtHot = atHot;
        if (!held && _probe) EndProbe();
        if (held && arrived && !Full && Mode is BodyMode.Rest or BodyMode.Badge) StartProbe();
        if (Mode == BodyMode.Drop)
        {
            // Отпустили — бросок уже обработан (Drop приходит раньше), или бросили мимо.
            if (!held)
            {
                if (_dropReleasedAt == 0) _dropReleasedAt = now;
                else if (now - _dropReleasedAt > 400) Sleep(restoreFocus: false);
            }
            else if (!inside && _cursorY > h + 120 && now - _dropLeftAt < 5000) Sleep(restoreFocus: false);
            return false;
        }

        if (esc && (Mode is BodyMode.Ribbon or BodyMode.Event || (Mode == BodyMode.Widget && GetForegroundWindow() == _win.Hwnd)))
        {
            Sleep();
            return false;
        }
        if (pressed && Mode == BodyMode.Widget && !inside && !OverOwnWindow(p))
        {
            // Клик снаружи закрывает; он сам уходит в окно, куда нажали (клики сквозь).
            Sleep(restoreFocus: false);
            return false;
        }
        switch (_gate.Step(now, atHot, inside, held, Full, Mode == BodyMode.Widget))
        {
            case HoverStep.Wake when Mode is BodyMode.Rest or BodyMode.Event or BodyMode.Badge: Wake(); break;
            case HoverStep.Sleep when Mode == BodyMode.Ribbon: Sleep(restoreFocus: false); break;
        }
        // Магнит: курсор под телом в его радиусе (принцип motion 5).
        return _cursorOn && _cursorY > h && _cursorY - h < 170 && cx < w / 2 + 40;
    }

    // ---------- приём файлов (П2, И9) ----------

    public bool Probing => _probe;

    void StartProbe()
    {
        if (_probe) return;
        _probe = true;
        LeaveRest();
        Draw();
        // Опрос должен увидеть отпускание кнопки где угодно, иначе полоса у края так и ловила бы клики.
        _host.PublishZones();
    }

    void EndProbe()
    {
        if (!_probe) return;
        _probe = false;
        if (Mode == BodyMode.Rest && !_moving) EnterRest();
        Draw();
        _host.PublishZones();
    }

    static bool HasFiles(DragEventArgs e) => e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);

    /// Открыт виджет полки или корзины — бросок прямо в него.
    DropTarget WidgetTarget => Mode == BodyMode.Widget ? Widget?.Id switch { "shelf" => DropTarget.Shelf, "bin" => DropTarget.Bin, _ => DropTarget.None } : DropTarget.None;

    void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e) || _disposed) return;
        if (Mode is BodyMode.Rest or BodyMode.Badge or BodyMode.Event && !Full)
        {
            _probe = false;
            _dropReleasedAt = 0;
            _dropLeftAt = 0;
            _host.Opening(this);
            _pillTimer.Stop();
            SetMode(BodyMode.Drop, DropView.W, DropView.H);
            Log.Write($"остров: приём файлов на {Screen.Name}");
        }
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        var target = Mode == BodyMode.Drop ? _drop.HitTest(e.GetPosition(_drop.View)) : WidgetTarget;
        _drop.Hover(target);
        e.AcceptedOperation = target switch
        {
            DropTarget.Bin => Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move,
            DropTarget.Shelf => Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link,
            _ => Windows.ApplicationModel.DataTransfer.DataPackageOperation.None,
        };
        e.DragUIOverride.Caption = target switch { DropTarget.Bin => "В корзину", DropTarget.Shelf => "На полку", _ => "" };
        e.DragUIOverride.IsCaptionVisible = target != DropTarget.None;
        e.Handled = true;
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        var target = Mode == BodyMode.Drop ? _drop.HitTest(e.GetPosition(_drop.View)) : WidgetTarget;
        if (target == DropTarget.None || !HasFiles(e)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0) _host.Dropped(this, target, paths);
        }
        catch (Exception ex) { Log.Error("остров: бросок файлов", ex); }
        finally { deferral.Complete(); }
    }

    // ---------- размер окна ----------

    /// Окно тела растёт под большое состояние (лист настроек) и сжимается обратно в покое.
    /// Центр верхнего края остаётся на месте, форма перерисовывается в новых координатах.
    void EnsureSize(double w, double h)
    {
        var maxW = (Screen.Bounds.Right - Screen.Bounds.Left) / Screen.Scale;
        var maxH = (Screen.Bounds.Bottom - Screen.Bounds.Top) / Screen.Scale;
        w = Math.Min(Math.Max(BaseW, w), maxW);
        h = Math.Min(Math.Max(BaseH, h), maxH);
        if (Math.Abs(w - WinW) < 1 && Math.Abs(h - WinH) < 1) return;
        WinW = w;
        WinH = h;
        Place();
        Canvas.SetLeft(_dot, WinW / 2 + RestW / 2 - 14);
        Canvas.SetLeft(_content, WinW / 2 - _targetW / 2);
        Draw();
        _host.PublishZones();
    }

    bool OverOwnWindow(POINT p)
    {
        var hit = GetAncestor(WindowFromPoint(p), 2 /* GA_ROOT */);
        return hit != IntPtr.Zero && hit != _win.Hwnd && ProcessId(hit) == (uint)Environment.ProcessId && ClassName(hit) != "WorkerW";
    }

    /// Где курсор интересен опросу (физические пиксели): тело, магнит под ним и зона наведения.
    public RECT Zone()
    {
        if (Full && Mode == BodyMode.Rest) return default;
        var s = Screen.Scale;
        var halfW = Math.Max(Math.Max(_w.Value, _w.Target) / 2 + 48, HotHalfW + 8) * s;
        var h = (Math.Max(_h.Value, _h.Target) + 180) * s;
        return new RECT { Left = (int)(CenterPx - halfW), Right = (int)(CenterPx + halfW), Top = Screen.Bounds.Top, Bottom = (int)(Screen.Bounds.Top + h) };
    }

    // ---------- кадры ----------

    /// Кадр анимации. true — тело ещё движется.
    public bool Frame(double dt)
    {
        if (_disposed) return false;
        var reduced = !AnimationsEnabled();
        if (!reduced && _moving)
        {
            _w.Step(dt, _p);
            _h.Step(dt, _p);
        }
        _magnet.Update(dt, _cursorX, _cursorY, _cursorOn, _w.Value, _h.Value, 0, Mode == BodyMode.Rest, reduced);
        if (_moving || _magnet.Moving) Draw();
        if (_moving && _w.Settled(0.3) && _h.Settled(0.3))
        {
            _w = new Spring { Value = _w.Target, Target = _w.Target };
            _h = new Spring { Value = _h.Target, Target = _h.Target };
            _moving = false;
            Draw();
            _host.PublishZones();
            if (Mode == BodyMode.Rest && !_probe) EnterRest();
            if (Mode is BodyMode.Rest or BodyMode.Badge) EnsureSize(BaseW, BaseH);
        }
        return _moving || _magnet.Moving;
    }

    void Draw()
    {
        if (_disposed) return;
        var w = Math.Max(40, _w.Value);
        var h = Math.Max(2, _h.Value);
        var s = Screen.Scale;
        var cx = WinW / 2;
        var pts = IslandShape.Outline(w, h, 0, _magnet);

        var seg = new PolyLineSegment();
        for (var i = 1; i < pts.Count; i++) seg.Points.Add(new Windows.Foundation.Point(pts[i].X + cx, pts[i].Y));
        var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(pts[0].X + cx, pts[0].Y), IsClosed = true, IsFilled = true };
        fig.Segments.Add(seg);
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        _shape.Data = geo;
        var rest = Mode == BodyMode.Rest && h < RestH + 1;
        // П21: идёт запись — кромка красная.
        _shape.Fill = B(rest && _s.Shots.Recording ? Red : Body);
        _dot.Visibility = rest && !_s.Shots.Recording && _s.Notifications.Unread && !_s.Notifications.DoNotDisturb ? Visibility.Visible : Visibility.Collapsed;

        // Область кликов = контур: вне его клики уходят окнам под островом.
        var poly = new POINT[pts.Count];
        for (var i = 0; i < pts.Count; i++) poly[i] = new POINT { X = (int)Math.Round((pts[i].X + cx) * s), Y = (int)Math.Round(pts[i].Y * s) };
        var rgn = CreatePolygonRgn(poly, poly.Length, 2 /* WINDING */);
        if (_probe)
        {
            // Приём файлов: у края полоса пошире кромки, чтобы перетаскивание её задело.
            var strip = CreateRectRgn((int)((cx - 170) * s), 0, (int)((cx + 170) * s), (int)(24 * s));
            CombineRgn(rgn, rgn, strip, 2 /* RGN_OR */);
            DeleteObject(strip);
        }
        if (SetWindowRgn(_win.Hwnd, rgn, true) == 0) DeleteObject(rgn);

        // Содержимое стоит; видно только то, что внутри тела.
        _content.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(_targetW / 2 - w / 2, 0, w, h) };
        var ribbonOpacity = Mode is BodyMode.Ribbon or BodyMode.Widget ? Math.Clamp((h - (RestH + 2.0 / 3 * 59)) / (59 / 3.0), 0, 1) : 0;
        _ribbon.Panel.Opacity = ribbonOpacity;
        var progress = Progress();
        if (Mode == BodyMode.Widget)
        {
            var o = _contentShown ? 1 : Math.Clamp((progress - 2.0 / 3) * 3, 0, 1);
            if (o >= 1) _contentShown = true;
            _scroll.Opacity = o;
        }
        if (Mode is BodyMode.Event or BodyMode.Badge) _pillHost.Opacity = Math.Clamp((progress - 2.0 / 3) * 3, 0, 1);
        if (Mode == BodyMode.Drop) _drop.View.Opacity = Math.Clamp((progress - 2.0 / 3) * 3, 0, 1);
    }

    double Progress()
    {
        static double P(double from, double value, double target) =>
            Math.Abs(target - from) < 1 ? 1 : Math.Clamp(1 - Math.Abs(target - value) / Math.Abs(target - from), 0, 1);
        return Math.Min(P(_fromW, _w.Value, _w.Target), P(_fromH, _h.Value, _h.Target));
    }

    public void Dispose()
    {
        if (_disposed) return;
        CloseWidget();
        _pillTimer.Stop();
        _chipTimer.Stop();
        Deactivate(restoreFocus: false);
        _disposed = true;
        try { _win.Window.Close(); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
}
