using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Ui;

/// Полоса оболочки у края монитора: док снизу или верхняя панель.
public interface IBar
{
    Screen Screen { get; }
    bool AtTop { get; }
    /// Закреплена: видна всегда и резервирует место (AppBar).
    bool Pinned { get; }
    /// Где курсор у края вызывает полосу (физические пиксели, по горизонтали).
    (int Left, int Right) EdgeSpan { get; }
    /// Где курсор считается «над полосой», пока она видна (физические пиксели).
    RECT HotRect { get; }
    /// Сколько места резервировать у края, когда полоса закреплена (физические пиксели).
    int Reserve { get; }
    /// Её окно, меню или окошко сейчас открыто: полоса не уезжает.
    bool Busy { get; }
    /// 0 — спрятана, 1 — показана.
    void SetReveal(double progress);
    /// Окна полосы (для AppBar берём первое).
    IntPtr MainHwnd { get; }
}

/// Автоскрытие и закрепление полос (раздел 1.6, 4, 9). Курсор опрашивает фоновый поток
/// 60 раз в секунду, а в поток интерфейса приходят только перемены: «упёрся в край»,
/// «ушёл с полосы». Полоса выезжает за 150 мс без задержки, уезжает через 600 мс после
/// ухода курсора. Над полноэкранной игрой или видео на её мониторе не выезжает совсем.
public sealed class Bars
{
    const int HideDelayMs = 600, SlideMs = 150;

    sealed class State
    {
        public required IBar Bar;
        public bool Shown;
        public double Progress;
        public long AwaySince;
        public long HoldUntil;
        public bool AtEdge;
        public bool Inside;
        /// Окно бросили, когда курсор был у этого края (снап к верху экрана): край снова
        /// вызывает полосу только после того, как курсор от него отойдёт.
        public bool EdgeSpent;
        public bool AppBar;
        /// Показана клавишей (Win, Win+X, Win+T) поверх полноэкранного окна: стоит, пока нужна.
        public bool Forced;
        public RECT AppBarRect;
        public int AppBarReserve;
        // Снимок для фонового потока.
        public volatile Geometry Geo = new();
    }

    sealed record Geometry(int EdgeY = 0, bool Top = false, int Left = 0, int Right = 0, RECT Hot = default, bool Visible = false, string Screen = "", bool Pinned = false);

    readonly DispatcherQueue _queue;
    readonly Monitors _monitors;
    readonly List<State> _bars = [];
    readonly DispatcherQueueTimer _tick;
    volatile State[] _snapshot = [];
    volatile HashSet<string> _fullscreen = [];
    bool _animating;
    long _lastFrame;
    bool _held;
    // Окно тащат за заголовок (или меняют размер): край не вызывает полосу, пока тащат. Раньше
    // после броска край молчал ещё секунду по таймеру, а пересчёта по её концу не было: курсор,
    // пришедший к краю в эту секунду, так и стоял у края без полосы до клика (WSH-94).
    bool _moving;
    readonly WinEventProc _moveSizeProc;

    /// Кнопка мыши зажата над полосой (тащат иконку): полоса не уезжает, пока её не отпустят.
    public bool Held => _held;

    public Bars(DispatcherQueue queue, Monitors monitors)
    {
        _queue = queue;
        _monitors = monitors;
        _tick = queue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(100);
        _tick.Tick += (_, _) => Evaluate();
        var t = new Thread(Poll) { IsBackground = true, Name = "bars" };
        t.Start();
        _moveSizeProc = (_, ev, _, _, _, _, _) =>
        {
            if (ev == 0x000A /* EVENT_SYSTEM_MOVESIZESTART */) _moving = true;
            else EndMove();
        };
        SetWinEventHook(0x000A, 0x000B, IntPtr.Zero, _moveSizeProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public void Add(IBar bar)
    {
        _bars.Add(new State { Bar = bar });
        Publish();
        Evaluate();
    }

    public void Remove(IBar bar)
    {
        var s = _bars.FirstOrDefault(b => b.Bar == bar);
        if (s is null) return;
        if (s.AppBar) AppBarRemove(bar.MainHwnd);
        _bars.Remove(s);
        Publish();
    }

    /// Убрать резерв места у всех полос (выход).
    public void Shutdown()
    {
        foreach (var s in _bars) if (s.AppBar) { AppBarRemove(s.Bar.MainHwnd); s.AppBar = false; }
    }

    /// Геометрия полос поменялась (раскладка, увеличение): обновить снимок для потока.
    public void Publish()
    {
        foreach (var s in _bars)
        {
            var b = s.Bar;
            var scr = b.Screen;
            s.Geo = new Geometry(b.AtTop ? scr.Bounds.Top : scr.Bounds.Bottom - 1, b.AtTop, b.EdgeSpan.Left, b.EdgeSpan.Right, b.HotRect, s.Progress > 0, scr.Name, b.Pinned);
        }
        _snapshot = [.. _bars];
    }

    /// Показана ли полоса сейчас.
    public bool IsShown(IBar bar) => _bars.FirstOrDefault(b => b.Bar == bar)?.Shown ?? false;

    /// Показать полосу сразу (переименование, запуск) — уедет как обычно, когда курсор уйдёт,
    /// но не раньше чем через `holdMs`.
    /// <param name="force">Вызвали клавишей (Пуск, Win+X, Win+T): выезжает и над полноэкранным окном.</param>
    public void Reveal(IBar bar, int holdMs = 0, bool force = false)
    {
        var s = _bars.FirstOrDefault(b => b.Bar == bar);
        if (s is null) return;
        if (IsFullscreen(bar.Screen) && !force) return;
        if (force) s.Forced = true;
        s.Shown = true;
        s.AwaySince = 0;
        s.HoldUntil = Environment.TickCount64 + holdMs;
        if (!_tick.IsRunning) _tick.Start();
        Kick();
    }

    /// Пересчитать (закрепление поменялось).
    public void Update() => Evaluate();

    public bool IsFullscreen(Screen screen) => _fullscreen.Contains(screen.Name);

    // ---------- фоновый поток ----------

    void Poll()
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4) /* PER_MONITOR_AWARE_V2 */);
        var tick = 0;
        var lastHeld = false;
        var screens = _monitors.All;
        while (true)
        {
            Thread.Sleep(16);
            tick++;
            try
            {
                if (tick % 15 == 0)
                {
                    screens = _monitors.All;
                    var full = FullscreenScreens(screens);
                    if (!full.SetEquals(_fullscreen))
                    {
                        _fullscreen = full;
                        _queue.TryEnqueue(Evaluate);
                    }
                }
                if (!GetCursorPos(out var p)) continue;
                var held = GetAsyncKeyState(0x01) < 0 || GetAsyncKeyState(0x02) < 0;
                var changed = held != lastHeld;
                lastHeld = held;
                foreach (var s in _snapshot)
                {
                    var g = s.Geo;
                    var atEdge = p.X >= g.Left && p.X < g.Right && (g.Top ? p.Y <= g.EdgeY : p.Y >= g.EdgeY)
                        // Край, за которым соседний монитор, не считается: курсор через него проскакивает.
                        && !Neighbour(screens, p.X, g.Top ? g.EdgeY - 1 : g.EdgeY + 1);
                    var inside = g.Visible && p.X >= g.Hot.Left && p.X < g.Hot.Right && p.Y >= g.Hot.Top && p.Y < g.Hot.Bottom;
                    if (atEdge != s.AtEdge || inside != s.Inside) changed = true;
                    s.AtEdge = atEdge;
                    s.Inside = inside;
                }
                if (changed)
                {
                    var h = held;
                    _queue.TryEnqueue(() => { OnHeld(h); Evaluate(); });
                }
            }
            catch
            {
                // Поток опроса не должен умирать.
            }
        }
    }

    static bool Neighbour(IReadOnlyList<Screen> screens, int x, int y)
    {
        foreach (var s in screens) if (s.Contains(x, y)) return true;
        return false;
    }

    /// Окно отпустили (или отпустили кнопку мыши — на случай, если конец перетаскивания не пришёл).
    void EndMove()
    {
        if (!_moving) return;
        _moving = false;
        foreach (var s in _bars) s.EdgeSpent = s.AtEdge;
        Evaluate();
    }

    void OnHeld(bool held)
    {
        if (!held) EndMove();
        // Зажали над полосой — держим её; отпустили — снова как обычно.
        if (held && !_held) _held = _bars.Any(b => b.Inside);
        if (!held) _held = false;
    }

    /// Мониторы, на которых сейчас полноэкранная игра или видео (не просто развёрнутое окно).
    HashSet<string> FullscreenScreens(IReadOnlyList<Screen> screens)
    {
        var set = new HashSet<string>();
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || ProcessId(fg) == (uint)Environment.ProcessId) return set;
        var cls = ClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return set;
        if (!GetWindowRect(fg, out var r)) return set;
        var screen = screens.FirstOrDefault(s => s.Contains((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2));
        if (screen is null) return set;
        SHQueryUserNotificationState(out var state);
        // 3 — Direct3D на весь экран, 4 — презентация.
        var exclusive = state is 3 or 4;
        var covers = r.Left <= screen.Bounds.Left && r.Top <= screen.Bounds.Top && r.Right >= screen.Bounds.Right && r.Bottom >= screen.Bounds.Bottom;
        // Развёрнутое окно тоже закрывает монитор, но это не игра (1.6): док выезжает над ним.
        // Панель задач спрятана, поэтому развёрнутое окно (Chrome) закрывает монитор целиком,
        // и Windows отвечает «2 — занят полноэкранным» — ему не верим (WSH-95). Полноэкранный
        // режим браузеров и видео не развёрнут: окно просто растянуто на монитор.
        if (exclusive || (covers && !IsZoomed(fg))) set.Add(screen.Name);
        return set;
    }

    // ---------- поток интерфейса ----------

    void Evaluate()
    {
        var now = Environment.TickCount64;
        var any = false;
        foreach (var s in _bars)
        {
            var b = s.Bar;
            var full = IsFullscreen(b.Screen);
            var was = s.Shown;
            if (!s.AtEdge) s.EdgeSpent = false;
            if (!s.Shown) s.Forced = false;
            if (full && !s.Forced) s.Shown = false;
            else if (b.Pinned) s.Shown = true;
            else if (s.Shown)
            {
                if (s.Inside || b.Busy || _held || s.AtEdge || now < s.HoldUntil) s.AwaySince = 0;
                else if (s.AwaySince == 0) s.AwaySince = now;
                else if (now - s.AwaySince >= HideDelayMs) { s.Shown = false; s.AwaySince = 0; s.Forced = false; }
            }
            else if (s.AtEdge && !_moving && !s.EdgeSpent) { s.Shown = true; s.AwaySince = 0; }
            if (s.Shown != was || (s.Shown ? s.Progress < 1 : s.Progress > 0)) any = true;
            UpdateAppBar(s, b.Pinned && !full);
            if (full && s.Forced && !s.Shown) s.Forced = false;
        }
        var waiting = _bars.Any(s => s.Shown && !s.Bar.Pinned);
        if (waiting) { if (!_tick.IsRunning) _tick.Start(); }
        else _tick.Stop();
        if (any) Kick();
    }

    void Kick()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, object e)
    {
        var now = Environment.TickCount64;
        var dt = _lastFrame == 0 ? 16 : Math.Clamp(now - _lastFrame, 0, 50);
        _lastFrame = now;
        var animate = AnimationsEnabled();
        var moving = false;
        foreach (var s in _bars)
        {
            var target = s.Shown ? 1.0 : 0.0;
            if (s.Progress == target) continue;
            // Если за краем соседний монитор, выезд был бы виден на нём: появляемся сразу.
            var instant = !animate || NeighbourBeyond(s.Bar);
            var step = instant ? 1 : dt / (double)SlideMs;
            s.Progress = s.Shown ? Math.Min(1, s.Progress + step) : Math.Max(0, s.Progress - step);
            s.Bar.SetReveal(EaseOut(s.Progress));
            moving |= s.Progress != target;
        }
        Publish();
        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    bool NeighbourBeyond(IBar bar)
    {
        var scr = bar.Screen;
        var (l, r) = (bar.HotRect.Left, bar.HotRect.Right);
        var y = bar.AtTop ? scr.Bounds.Top - 1 : scr.Bounds.Bottom;
        return _monitors.All.Any(o => o != scr && y >= o.Bounds.Top && y < o.Bounds.Bottom && o.Bounds.Left < r && o.Bounds.Right > l);
    }

    // ---------- AppBar ----------

    void UpdateAppBar(State s, bool want)
    {
        var h = s.Bar.MainHwnd;
        if (!want)
        {
            if (s.AppBar) { AppBarRemove(h); s.AppBar = false; }
            return;
        }
        var reserve = s.Bar.Reserve;
        var scr = s.Bar.Screen.Bounds;
        if (s.AppBar && s.AppBarReserve == reserve && Same(s.AppBarRect, scr)) return;
        var d = Data(h, s.Bar.AtTop, scr);
        if (!s.AppBar) SHAppBarMessage(ABM_NEW, ref d);
        if (s.Bar.AtTop) d.rc.Bottom = d.rc.Top + reserve; else d.rc.Top = d.rc.Bottom - reserve;
        SHAppBarMessage(ABM_QUERYPOS, ref d);
        if (s.Bar.AtTop) d.rc.Bottom = d.rc.Top + reserve; else d.rc.Top = d.rc.Bottom - reserve;
        SHAppBarMessage(ABM_SETPOS, ref d);
        s.AppBar = true;
        s.AppBarRect = scr;
        s.AppBarReserve = reserve;
    }

    static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    static APPBARDATA Data(IntPtr h, bool top, RECT rc) => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = h,
        uEdge = top ? 1u : 3u, // ABE_TOP, ABE_BOTTOM
        rc = rc,
    };

    static void AppBarRemove(IntPtr h)
    {
        var d = Data(h, false, default);
        SHAppBarMessage(ABM_REMOVE, ref d);
    }

    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
}
