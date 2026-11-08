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

public enum BodyMode { Rest, Ribbon, Widget, Event }

/// Тело острова на одном мониторе (путь b1 из пробы E22): одно неподвижное прозрачное окно
/// с запасом; форма — путь по контуру IslandShape, клики — SetWindowRgn по тому же контуру,
/// в покое окно сквозное для мыши. Кромка → лента → виджет → пилюля события — одно тело,
/// меняющее форму на пружинах; содержимое не двигается, проявляется на последней трети пути.
public sealed class IslandBody
{
    const double WinW = 680, WinH = 640, MagnetPad = 60, RestW = 190, RestH = 3, HotY = 2, HotHalfW = 140, Pad = 18;
    const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;

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
        _content = new Grid { IsHitTestVisible = false, Children = { stack, _pillHost, _chip } };
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
        if (Mode == BodyMode.Rest || _disposed) return;
        CloseWidget();
        _pillTimer.Stop();
        _pillClick = null;
        _chip.Visibility = Visibility.Collapsed;
        _gate.Reset();
        Deactivate(restoreFocus);
        SetMode(BodyMode.Rest, RestW, RestH);
    }

    /// Открыть виджет в теле. byUser — кликом (берём фокус: Escape, Tab, ввод пароля).
    public void OpenWidget(string id, bool byUser)
    {
        if (_disposed) return;
        if (Full && !byUser) return;
        var w = _widgets.TryGetValue(id, out var existing) ? existing : _widgets[id] = IslandWidgets.Create(id, _s, this);
        _host.Opening(this);
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
        if (byUser) Activate();
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
        var maxContent = WinH - MagnetPad - RibbonView.Height;
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
        if (Mode == BodyMode.Ribbon) return; // лента сама показывает то же на значке
        _pillHost.Child = content;
        _pillClick = click;
        _pillUntil = Environment.TickCount64 + ms;
        if (_hidden) { _win.Show(false); _hidden = false; }
        _win.Raise();
        _raisedAt = Environment.TickCount64;
        _pillHost.Visibility = Visibility.Visible;
        _pillHost.Measure(new Windows.Foundation.Size(WinW, 200));
        var size = _pillHost.DesiredSize;
        SetMode(BodyMode.Event, Math.Max(64, size.Width), Math.Max(30, size.Height));
        _pillTimer.Start();
    }

    void PillTick()
    {
        if (Mode != BodyMode.Event) { _pillTimer.Stop(); return; }
        var now = Environment.TickCount64;
        // Игра тоже бывает «поверх всех»: раз в секунду поднимаемся над ней.
        if (now - _raisedAt > 1000) { _win.Raise(); _raisedAt = now; }
        var hover = _cursorOn && _cursorY <= _h.Value + 4 && Math.Abs(_cursorX) <= _w.Value / 2 + 4;
        if (hover) _pillUntil = Math.Max(_pillUntil, now + 600);
        if (now >= _pillUntil) Sleep(restoreFocus: false);
    }

    void SetMode(BodyMode m, double w, double h)
    {
        var from = Mode;
        _p = m is BodyMode.Rest or BodyMode.Ribbon or BodyMode.Event ? Springs.Ribbon
            : from == BodyMode.Widget ? Springs.Switch : Springs.Widget;
        Mode = m;
        _fromW = _w.Value;
        _fromH = _h.Value;
        _targetW = w;
        _content.Width = w;
        Canvas.SetLeft(_content, WinW / 2 - w / 2);
        _ribbon.Panel.Visibility = m is BodyMode.Ribbon or BodyMode.Widget ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = m == BodyMode.Widget ? Visibility.Visible : Visibility.Collapsed;
        if (m != BodyMode.Event) { _pillHost.Visibility = Visibility.Collapsed; _pillHost.Child = null; }
        if (m != BodyMode.Widget) _chip.Visibility = Visibility.Collapsed;
        // Уходит быстрее, чем приходит: старое содержимое гаснет сразу.
        if (from != m || m == BodyMode.Widget) { _scroll.Opacity = 0; _pillHost.Opacity = 0; }
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
            case HoverStep.Wake when Mode is BodyMode.Rest or BodyMode.Event: Wake(); break;
            case HoverStep.Sleep when Mode == BodyMode.Ribbon: Sleep(restoreFocus: false); break;
        }
        // Магнит: курсор под телом в его радиусе (принцип motion 5).
        return _cursorOn && _cursorY > h && _cursorY - h < 170 && cx < w / 2 + 40;
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
            if (Mode == BodyMode.Rest) EnterRest();
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
        if (Mode == BodyMode.Event) _pillHost.Opacity = Math.Clamp((progress - 2.0 / 3) * 3, 0, 1);
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
