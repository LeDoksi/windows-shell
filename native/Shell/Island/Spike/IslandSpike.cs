using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WindowsShell.Island.Core;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using XPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WindowsShell.Island.Spike;

/// Проба тела острова (И1): остров на каждом мониторе — кромка, лента и три виджета-заглушки.
/// Меряет, плавно ли меняется форма, как быстро тело отвечает на нажатие и проходят ли клики
/// сквозь остров. Две стратегии окна: a — окно меняет размер каждый кадр, b — окно неподвижно,
/// меняются контур и область кликов. Покой: окно сквозное (b1) или кромку рисует отдельное
/// слоистое окно, а WinUI-окно скрыто (b2). Отчёт — island-spike-{a|b}.txt рядом с exe.
internal sealed class IslandSpike
{
    const double WinPadX = 8, MagnetPad = 60, HotY = 18, HotHalfW = 140, ContentW = 600, ContentH = 300;
    const int HoverMs = 150, LeaveMs = 400;
    static readonly Windows.UI.Color BodyColor = ColorHelper.FromArgb(255, 0x0A, 0x0A, 0x0B);
    static readonly IslandMode[] AutoSequence = [IslandMode.Ribbon, IslandMode.Sound, IslandMode.Net, IslandMode.Media, IslandMode.Ribbon, IslandMode.Rest];

    readonly SpikeOptions _opt;
    readonly DispatcherQueue _queue;
    readonly SpikeMetrics _metrics;
    readonly List<Body> _bodies = [];
    readonly bool _reduced = !AnimationsEnabled();
    volatile RECT[] _zones = [];
    volatile bool _timersPending;
    volatile bool _stop;
    bool _b2;
    bool _rendering, _firstFrame, _lastHeld, _lastEsc, _finished;
    long _lastFrame;
    DispatcherQueueTimer? _limit;

    public IslandSpike(SpikeOptions opt, DispatcherQueue queue)
    {
        _opt = opt;
        _queue = queue;
        _metrics = new SpikeMetrics(opt.Strategy);
    }

    sealed class Body
    {
        public required Screen Screen;
        public required ShellWindow Win;
        public required XPath Shape;
        public required Grid Content;
        public required StackPanel Ribbon;
        public required TextBlock WidgetText;
        public required double PeriodMs;
        public IslandMode Mode = IslandMode.Rest;
        public Spring W = new() { Value = 190, Target = 190 };
        public Spring H = new() { Value = 3, Target = 3 };
        public SpringParams P = Springs.Ribbon;
        public double FromW = 190, FromH = 3;
        public readonly Magnet Magnet = new();
        public long HoverSince, LeaveSince;
        public double CursorX, CursorY;
        public bool CursorOn, Moving, Transparent, Hidden, OpenChecked;
        public double WinW, WinH;
        public LayeredWindow? Rim;

        public int CenterPx => (int)(((long)Screen.Bounds.Left + Screen.Bounds.Right) / 2);
        public RectI Monitor => new(Screen.Bounds.Left, Screen.Bounds.Top, Screen.Bounds.Right, Screen.Bounds.Bottom);
    }

    public void Start()
    {
        var monitors = new Monitors(_queue, new MessageWindow());
        _metrics.Line($"== Мониторы ({monitors.All.Count})");
        foreach (var s in monitors.All)
        {
            var hz = RefreshRate(s.Name);
            _metrics.Line($"   {s.Name}: {s.Bounds.Width}×{s.Bounds.Height} в ({s.Bounds.Left}, {s.Bounds.Top}), масштаб {s.Scale * 100:0} %, {(hz > 0 ? $"{hz} Гц" : "частота неизвестна")}{(s.Primary ? ", основной" : "")}");
            _bodies.Add(CreateBody(s, hz > 0 ? 1000.0 / hz : 1000.0 / 60));
        }
        _metrics.Line($"== Настройки: стратегия {_opt.Strategy}, {(_opt.Auto ? "авторежим" : "ручной режим")}, эффекты анимации Windows {(_reduced ? "выключены (без пружин)" : "включены")}");
        foreach (var b in _bodies) EnterRest(b);
        PublishZones();
        new Thread(Poll) { IsBackground = true, Name = "проба острова: курсор" }.Start();
        // Окнам нужно время, чтобы появиться, прежде чем смотреть на пиксели и клики.
        After(900, CheckRestAll);
        if (_opt.Auto) After(1500, RunAuto);
        else
        {
            _limit = _queue.CreateTimer();
            _limit.Interval = TimeSpan.FromMinutes(10);
            _limit.IsRepeating = false;
            _limit.Tick += (_, _) => Finish(null);
            _limit.Start();
        }
    }

    void After(int ms, Action a)
    {
        var t = _queue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.IsRepeating = false;
        t.Tick += (_, _) => a();
        t.Start();
    }

    Body CreateBody(Screen s, double periodMs)
    {
        var win = new ShellWindow(Surface.Transparent, noActivate: false, rounded: false, transitions: false);
        var canvas = new Canvas();
        win.Root.Children.Add(canvas);
        var shape = new XPath { Fill = new SolidColorBrush(BodyColor) };
        canvas.Children.Add(shape);

        var content = new Grid { Width = ContentW, Height = ContentH, IsHitTestVisible = false };
        var ribbon = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 13, 0, 0),
            Spacing = 8,
            Opacity = 0,
        };
        var text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 96, 0, 0),
            FontFamily = Theme.Font,
            FontSize = 15,
            Foreground = Theme.Brush(Theme.TextSecondary),
            Opacity = 0,
        };
        content.Children.Add(ribbon);
        content.Children.Add(text);
        canvas.Children.Add(content);

        var body = new Body { Screen = s, Win = win, Shape = shape, Content = content, Ribbon = ribbon, WidgetText = text, PeriodMs = periodMs };
        ribbon.Children.Add(Btn("Звук", () => SetMode(body, IslandMode.Sound, SpikeMetrics.Now, true)));
        ribbon.Children.Add(Btn("Сеть", () => SetMode(body, IslandMode.Net, SpikeMetrics.Now, true)));
        ribbon.Children.Add(Btn("Медиа", () => SetMode(body, IslandMode.Media, SpikeMetrics.Now, true)));
        ribbon.Children.Add(Btn("×", () => Finish(null)));

        if (_opt.Strategy == 'a')
        {
            body.WinW = Math.Ceiling(190 + 2 * WinPadX);
            body.WinH = Math.Ceiling(3 + MagnetPad);
        }
        else
        {
            // Неподвижное окно под самый большой виджет и магнит.
            body.WinW = IslandSizes.Of(IslandMode.Net).W + 2 * WinPadX;
            body.WinH = IslandSizes.Of(IslandMode.Net).H + MagnetPad;
        }
        var r = IslandLayout.Place(body.Monitor, s.Scale, body.WinW, body.WinH);
        win.Place(r.Left, r.Top, r.Width, r.Height);
        // Слоистое окно — чтобы в покое сделать его сквозным для мыши (b1).
        AddExStyle(win.Hwnd, WS_EX_LAYERED);
        SetLayeredWindowAttributes(win.Hwnd, 0, 255, LWA_ALPHA);
        win.Show(false);
        Draw(body);
        return body;
    }

    static Button Btn(string text, Action act)
    {
        var b = new Button { Content = text, MinWidth = 96, Height = 36, FontFamily = Theme.Font };
        // Форма меняется по нажатию, а не по отпусканию: так меряется задержка «нажатие → кадр».
        b.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) act();
        }), true);
        // Клавиатура и диктор.
        b.Click += (_, _) => act();
        return b;
    }

    static string Name(IslandMode m) => m switch
    {
        IslandMode.Rest => "покой",
        IslandMode.Ribbon => "лента",
        IslandMode.Sound => "звук",
        IslandMode.Net => "сеть",
        _ => "медиа",
    };

    void SetMode(Body b, IslandMode m, long start, bool byPointer)
    {
        if (_finished || b.Mode == m) return;
        var from = b.Mode;
        b.P = m is IslandMode.Rest or IslandMode.Ribbon ? Springs.Ribbon
            : from is IslandMode.Rest or IslandMode.Ribbon ? Springs.Widget : Springs.Switch;
        b.Mode = m;
        b.FromW = b.W.Value;
        b.FromH = b.H.Value;
        var (w, h) = IslandSizes.Of(m);
        b.W.Target = w;
        b.H.Target = h;
        if (_reduced)
        {
            b.W = new Spring { Value = w, Target = w };
            b.H = new Spring { Value = h, Target = h };
        }
        b.HoverSince = b.LeaveSince = 0;
        if (m != IslandMode.Rest) LeaveRest(b);
        if (m is IslandMode.Sound or IslandMode.Net or IslandMode.Media) b.WidgetText.Text = $"{Name(m)} — заглушка виджета";
        b.Content.IsHitTestVisible = m != IslandMode.Rest;
        _metrics.Begin(b.Screen.Name, Name(from), Name(m), start, byPointer, b.PeriodMs);
        b.Moving = true;
        PublishZones();
        EnsureRendering();
    }

    // ---------- покой: сквозное окно (b1) или отдельная кромка (b2) ----------

    void EnterRest(Body b)
    {
        if (_b2)
        {
            b.Win.Hide();
            b.Hidden = true;
            ShowRim(b);
            return;
        }
        AddExStyle(b.Win.Hwnd, WS_EX_TRANSPARENT);
        b.Transparent = true;
    }

    void LeaveRest(Body b)
    {
        if (b.Hidden)
        {
            b.Rim?.Hide();
            b.Win.Show(false);
            b.Win.Raise();
            b.Hidden = false;
        }
        if (b.Transparent)
        {
            AddExStyle(b.Win.Hwnd, 0, WS_EX_TRANSPARENT);
            b.Transparent = false;
        }
    }

    void ShowRim(Body b)
    {
        var s = b.Screen.Scale;
        var w = (int)Math.Round(190 * s);
        var h = Math.Max(1, (int)Math.Round(3 * s));
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(System.Drawing.Color.FromArgb(255, 0x0A, 0x0A, 0x0B));
        b.Rim ??= new LayeredWindow(excludeFromCapture: false);
        b.Rim.Show(bmp, b.CenterPx - w / 2, b.Screen.Bounds.Top);
        b.Rim.Raise();
    }

    /// Видна ли кромка (пиксель экрана того же цвета, что тело) и проходят ли клики сквозь неё.
    /// Если кромки не видно или клики не проходят, переходим на b2 и проверяем ещё раз.
    void CheckRestAll()
    {
        var bad = false;
        foreach (var b in _bodies)
        {
            var (visible, through) = CheckRest(b, _b2 ? "b2" : "b1");
            if (visible == false || !through) bad = true;
        }
        if (bad && !_b2)
        {
            _metrics.Check("b1 не подошёл → b2: в покое WinUI-окно скрыто, кромку рисует слоистое окно");
            _b2 = true;
            foreach (var b in _bodies)
            {
                AddExStyle(b.Win.Hwnd, 0, WS_EX_TRANSPARENT | WS_EX_LAYERED);
                b.Transparent = false;
                if (b.Mode == IslandMode.Rest && !b.Moving) EnterRest(b);
            }
            After(700, () => { foreach (var b in _bodies) CheckRest(b, "b2"); });
        }
        else _metrics.Check($"вариант покоя: {(_b2 ? "b2" : "b1")}");
    }

    (bool? Visible, bool Through) CheckRest(Body b, string variant)
    {
        var x = b.CenterPx;
        var y = b.Screen.Bounds.Top + 1;
        var rim = ScreenPixel(x, y);
        var below = ScreenPixel(x, b.Screen.Bounds.Top + (int)Math.Round(3 * b.Screen.Scale) + 6);
        bool? visible = Near(rim, BodyColor) ? (Near(below, BodyColor) ? null : true) : false;
        var hit = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /* GA_ROOT */);
        var through = hit != b.Win.Hwnd && (b.Rim is null || hit != b.Rim.Hwnd);
        _metrics.Check($"{b.Screen.Name} покой ({variant}): кромка видна: {(visible is null ? "не ясно (фон под ней такой же тёмный)" : visible.Value ? "да" : "нет")} " +
            $"(пиксель {rim:X6}, ниже {below:X6}), клики сквозь: {(through ? "да" : "нет")}");
        return (visible, through);
    }

    void CheckOpen(Body b)
    {
        b.OpenChecked = true;
        var inside = new POINT { X = b.CenterPx, Y = b.Screen.Bounds.Top + (int)Math.Round(100 * b.Screen.Scale) };
        var inHit = GetAncestor(WindowFromPoint(inside), 2) == b.Win.Hwnd;
        GetWindowRect(b.Win.Hwnd, out var r);
        var corner = new POINT { X = r.Left + 3, Y = r.Bottom - 3 };
        var cornerHit = GetAncestor(WindowFromPoint(corner), 2) == b.Win.Hwnd;
        _metrics.Check($"{b.Screen.Name} открыто ({Name(b.Mode)}): внутри контура — окно острова: {(inHit ? "да" : "нет")}, " +
            $"угол окна вне контура — сквозь: {(cornerHit ? "нет" : "да")} (окно {r.Width}×{r.Height})");
    }

    static bool Near(int rgb, Windows.UI.Color c) =>
        Math.Abs(((rgb >> 16) & 0xFF) - c.R) <= 12 && Math.Abs(((rgb >> 8) & 0xFF) - c.G) <= 12 && Math.Abs((rgb & 0xFF) - c.B) <= 12;

    /// Цвет пикселя экрана как 0xRRGGBB.
    static int ScreenPixel(int x, int y)
    {
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var bgr = GetPixel(dc, x, y);
            return (int)(((bgr & 0xFF) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    // ---------- курсор ----------

    /// Опрос курсора раз в 16 мс в фоне (как Ui/Bars). В поток интерфейса — только когда курсор
    /// двигается возле острова, меняются кнопки или идёт таймер наведения/ухода.
    void Poll()
    {
        POINT last = default;
        bool lastNear = false, lastHeld = false, lastEsc = false;
        while (!_stop)
        {
            Thread.Sleep(16);
            try
            {
                if (!GetCursorPos(out var p)) continue;
                var held = (GetAsyncKeyState(0x01) | GetAsyncKeyState(0x02)) < 0;
                var esc = GetAsyncKeyState(VK_ESCAPE) < 0;
                var near = false;
                foreach (var z in _zones)
                    if (p.X >= z.Left && p.X < z.Right && p.Y >= z.Top && p.Y < z.Bottom) { near = true; break; }
                var moved = p.X != last.X || p.Y != last.Y;
                if ((near && moved) || near != lastNear || held != lastHeld || esc != lastEsc || _timersPending)
                    _queue.TryEnqueue(() => Tick(p, held, esc));
                last = p;
                lastNear = near;
                lastHeld = held;
                lastEsc = esc;
            }
            catch
            {
                // Поток опроса не должен умирать.
            }
        }
    }

    void Tick(POINT p, bool held, bool esc)
    {
        if (_finished) return;
        var pressed = held && !_lastHeld;
        var escDown = esc && !_lastEsc;
        _lastHeld = held;
        _lastEsc = esc;
        var now = SpikeMetrics.Now;
        var near = false;
        foreach (var b in _bodies)
        {
            var s = b.Screen.Scale;
            b.CursorX = (p.X - b.CenterPx) / s;
            b.CursorY = (p.Y - b.Screen.Bounds.Top) / s;
            b.CursorOn = b.Screen.Contains(p.X, p.Y);
            var cx = Math.Abs(b.CursorX);
            if (b.Mode == IslandMode.Rest)
            {
                // П1: пока зажата кнопка мыши (тянут окно, выделяют), остров не раскрывается.
                if (b.CursorOn && b.CursorY < HotY && cx < HotHalfW && !held)
                {
                    if (b.HoverSince == 0) b.HoverSince = now;
                    else if (SpikeMetrics.Ms(b.HoverSince, now) >= HoverMs) SetMode(b, IslandMode.Ribbon, now, true);
                }
                else b.HoverSince = 0;
            }
            else
            {
                var inside = b.CursorOn && b.CursorY <= b.H.Value + 8 && cx <= b.W.Value / 2 + 8;
                if (escDown || (pressed && !inside))
                {
                    SetMode(b, IslandMode.Rest, now, true);
                }
                else if (b.Mode == IslandMode.Ribbon)
                {
                    if (!inside)
                    {
                        if (b.LeaveSince == 0) b.LeaveSince = now;
                        else if (SpikeMetrics.Ms(b.LeaveSince, now) >= LeaveMs) SetMode(b, IslandMode.Rest, now, true);
                    }
                    else b.LeaveSince = 0;
                }
            }
            // Магнит: курсор под телом в его радиусе.
            if (b.CursorOn && b.CursorY > b.H.Value && b.CursorY - b.H.Value < 170 && cx < b.W.Value / 2 + 40) near = true;
        }
        _timersPending = _bodies.Any(b => b.HoverSince != 0 || b.LeaveSince != 0);
        if (near) EnsureRendering();
    }

    /// Где курсор интересен опросу: тело, магнит под ним и зона наведения, в пикселях.
    void PublishZones()
    {
        var zones = new RECT[_bodies.Count];
        for (var i = 0; i < _bodies.Count; i++)
        {
            var b = _bodies[i];
            var s = b.Screen.Scale;
            var halfW = Math.Max(Math.Max(b.W.Value, b.W.Target) / 2 + 48, HotHalfW + 8) * s;
            var h = (Math.Max(b.H.Value, b.H.Target) + 180) * s;
            zones[i] = new RECT { Left = (int)(b.CenterPx - halfW), Right = (int)(b.CenterPx + halfW), Top = b.Screen.Bounds.Top, Bottom = (int)(b.Screen.Bounds.Top + h) };
        }
        _zones = zones;
    }

    // ---------- кадры ----------

    void EnsureRendering()
    {
        if (_rendering || _finished) return;
        _rendering = true;
        _firstFrame = true;
        _lastFrame = SpikeMetrics.Now;
        CompositionTarget.Rendering += OnRendering;
    }

    void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? sender, object e)
    {
        try
        {
            var now = SpikeMetrics.Now;
            var frameMs = SpikeMetrics.Ms(_lastFrame, now);
            _lastFrame = now;
            var first = _firstFrame;
            _firstFrame = false;
            var dt = Math.Min(frameMs / 1000, first ? 1 / 60.0 : 0.5);
            var any = false;
            foreach (var b in _bodies)
            {
                var (oldW, oldH) = (b.W.Value, b.H.Value);
                if (!_reduced)
                {
                    b.W.Step(dt, b.P);
                    b.H.Step(dt, b.P);
                }
                b.Magnet.Update(dt, b.CursorX, b.CursorY, b.CursorOn && !b.Hidden, b.W.Value, b.H.Value, 0, b.Mode == IslandMode.Rest, _reduced);
                var changed = Math.Abs(b.W.Value - oldW) > 0.01 || Math.Abs(b.H.Value - oldH) > 0.01;
                if (b.Moving || b.Magnet.Moving) Draw(b);
                if (b.Moving)
                {
                    _metrics.Frame(b.Screen.Name, frameMs, first, changed || _reduced);
                    if (b.W.Settled(0.3) && b.H.Settled(0.3))
                    {
                        b.W = new Spring { Value = b.W.Target, Target = b.W.Target };
                        b.H = new Spring { Value = b.H.Target, Target = b.H.Target };
                        b.Moving = false;
                        Draw(b);
                        _metrics.Settled(b.Screen.Name);
                        PublishZones();
                        if (b.Mode == IslandMode.Rest) EnterRest(b);
                        else if (!b.OpenChecked && b.Mode != IslandMode.Ribbon) After(150, () => { if (!b.Moving && b.Mode != IslandMode.Rest) CheckOpen(b); });
                    }
                }
                any |= b.Moving || b.Magnet.Moving;
            }
            if (!any) StopRendering();
        }
        catch (Exception ex)
        {
            Log.Error("проба острова: кадр", ex);
            StopRendering();
        }
    }

    void Draw(Body b)
    {
        var w = Math.Max(40, b.W.Value);
        var h = Math.Max(2, b.H.Value);
        var s = b.Screen.Scale;
        if (_opt.Strategy == 'a')
        {
            // Стратегия a: окно — рамка контура плюс запас под магнит, двигаем каждый кадр.
            b.WinW = Math.Ceiling(w + 2 * WinPadX);
            b.WinH = Math.Ceiling(h + MagnetPad);
            var r = IslandLayout.Place(b.Monitor, s, b.WinW, b.WinH);
            b.Win.Move(r.Left, r.Top, r.Width, r.Height);
        }
        var cx = b.WinW / 2;
        var pts = IslandShape.Outline(w, h, 0, b.Magnet);

        var seg = new PolyLineSegment();
        for (var i = 1; i < pts.Count; i++) seg.Points.Add(new Windows.Foundation.Point(pts[i].X + cx, pts[i].Y));
        var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(pts[0].X + cx, pts[0].Y), IsClosed = true, IsFilled = true };
        fig.Segments.Add(seg);
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        b.Shape.Data = geo;

        // Область кликов окна = контур: вне контура клики уходят окнам под островом.
        var poly = new POINT[pts.Count];
        for (var i = 0; i < pts.Count; i++) poly[i] = new POINT { X = (int)Math.Round((pts[i].X + cx) * s), Y = (int)Math.Round(pts[i].Y * s) };
        var rgn = CreatePolygonRgn(poly, poly.Length, WINDING);
        if (SetWindowRgn(b.Win.Hwnd, rgn, true) == 0) DeleteObject(rgn);

        Canvas.SetLeft(b.Content, cx - ContentW / 2);
        // Текст и кнопки стоят на месте; проявляются на последней трети пути.
        b.Ribbon.Opacity = Math.Clamp((h - (3 + 2.0 / 3 * 59)) / (59 / 3.0), 0, 1);
        b.WidgetText.Opacity = b.Mode is IslandMode.Sound or IslandMode.Net or IslandMode.Media ? Math.Clamp((Progress(b) - 2.0 / 3) * 3, 0, 1) : 0;
    }

    static double Progress(Body b)
    {
        double P(double from, double value, double target) =>
            Math.Abs(target - from) < 1 ? 1 : Math.Clamp(1 - Math.Abs(target - value) / Math.Abs(target - from), 0, 1);
        return Math.Min(P(b.FromW, b.W.Value, b.W.Target), P(b.FromH, b.H.Value, b.H.Target));
    }

    // ---------- авторежим и конец ----------

    async void RunAuto()
    {
        try
        {
            for (var round = 0; round < 10 && !_finished; round++)
                foreach (var m in AutoSequence)
                {
                    foreach (var b in _bodies) SetMode(b, m, SpikeMetrics.Now, byPointer: false);
                    await Task.Delay(1200);
                }
            foreach (var b in _bodies) CheckRest(b, _b2 ? "b2" : "b1");
            await Task.Delay(10_000);
            var proc = Process.GetCurrentProcess();
            var t0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            await Task.Delay(60_000);
            proc.Refresh();
            var cpu = (proc.TotalProcessorTime - t0).TotalMilliseconds / (sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
            Finish($"{cpu.ToString("0.000", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} % от всех ядер ({Environment.ProcessorCount}) за {sw.Elapsed.TotalSeconds:0} с (цель ≈ 0,02 %)");
        }
        catch (Exception e)
        {
            Log.Error("проба острова: авторежим", e);
            Finish(null);
        }
    }

    void Finish(string? cpuLine)
    {
        if (_finished) return;
        _finished = true;
        _stop = true;
        StopRendering();
        try
        {
            var file = System.IO.Path.Combine(AppContext.BaseDirectory, $"island-spike-{_opt.Strategy}.txt");
            File.WriteAllText(file, _metrics.Report(cpuLine));
            Log.Write($"проба острова: отчёт {file}");
        }
        catch (Exception e) { Log.Error("проба острова: отчёт", e); }
        foreach (var b in _bodies)
        {
            b.Rim?.Dispose();
            try { b.Win.Window.Close(); } catch { }
        }
        Application.Current.Exit();
    }

    const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    const uint LWA_ALPHA = 2;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
}
