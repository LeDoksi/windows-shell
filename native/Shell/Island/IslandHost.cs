using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsShell.Charge;
using WindowsShell.Island.Core;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Island.IslandUi;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Island;

/// Остров у верхнего края каждого монитора (WSH-E23, волна 1). Тела по мониторам, один
/// раскрытый остров, опрос курсора в фоне 60 раз в секунду (в поток интерфейса — только
/// перемены возле острова и нажатия, пока что-то открыто), fullscreen по мониторам,
/// события «раскладка» и «низкий заряд». В покое кадров нет: CompositionTarget.Rendering
/// подписан, только пока что-то движется.
public sealed class IslandHost
{
    readonly Services _s;
    readonly Dictionary<string, IslandBody> _bodies = [];
    readonly FullscreenWatch _full = new();
    volatile RECT[] _zones = [];
    volatile bool _active, _timers, _running;
    bool _rendering, _lastHeld, _lastEsc, _started;
    long _lastFrame;
    string _lastLang = "";

    public IslandHost(Services s) => _s = s;

    public bool Enabled => _s.Config.Island.Enabled;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _s.Config.Changed += Apply;
        _s.Monitors.Changed += () => { if (_running) Sync(); };
        _s.Messages.Resumed += () => { foreach (var b in _bodies.Values) b.Sleep(restoreFocus: false); };
        _s.Messages.TaskbarCreated += () => _s.Queue.TryEnqueue(() => { foreach (var b in _bodies.Values) b.Raise(); });
        void Indicators() { foreach (var b in _bodies.Values) b.RefreshIndicators(); }
        _s.Audio.Changed += Indicators;
        _s.Network.Changed += Indicators;
        _s.Media.Changed += Indicators;
        _s.Charge.Changed += Indicators;
        _s.Shots.Changed += Indicators;
        _s.Notifications.Changed += Indicators;
        _lastLang = _s.Language.Current.Short;
        _s.Language.Changed += () => { Indicators(); LanguageChanged(); };
        new Thread(Poll) { IsBackground = true, Name = "остров" }.Start();
        Apply();
    }

    /// Включить или выключить по настройке (на лету).
    void Apply()
    {
        if (Enabled == _running)
        {
            if (_running) foreach (var b in _bodies.Values) b.RefreshIndicators();
            return;
        }
        _running = Enabled;
        if (_running)
        {
            Sync();
            Log.Write($"остров: включён, мониторов {_bodies.Count}");
        }
        else
        {
            foreach (var b in _bodies.Values) b.Dispose();
            _bodies.Clear();
            _zones = [];
            Log.Write("остров: выключен");
        }
    }

    public void Stop()
    {
        _running = false;
        foreach (var b in _bodies.Values) b.Dispose();
        _bodies.Clear();
    }

    /// Тело на каждом мониторе; пропавший монитор — его тело (даже раскрытое) убирается.
    void Sync()
    {
        try
        {
            var screens = _s.Monitors.All;
            foreach (var name in _bodies.Keys.Where(n => screens.All(s => s.Name != n)).ToList())
            {
                _bodies[name].Dispose();
                _bodies.Remove(name);
                Log.Write($"остров: монитор {name} отключён");
            }
            foreach (var screen in screens)
            {
                if (_bodies.TryGetValue(screen.Name, out var b)) b.Move(screen);
                else
                {
                    b = _bodies[screen.Name] = new IslandBody(this, _s, screen);
                    b.SetFull(_full.Full.Contains(screen.Name));
                }
            }
            PublishZones();
        }
        catch (Exception e) { Log.Error("остров: мониторы", e); }
    }

    // ---------- открытие ----------

    /// Тело раскрывается: остальные сворачиваются (раскрыт один остров).
    public void Opening(IslandBody body)
    {
        foreach (var b in _bodies.Values)
            if (b != body && b.Mode is BodyMode.Ribbon or BodyMode.Widget) b.Sleep(restoreFocus: false);
    }

    public void SlotClicked(IslandBody body, string id)
    {
        if (id == "settings")
        {
            body.Sleep(restoreFocus: false);
            // Настройки внутри острова — волна 2; пока окно настроек на странице «Остров».
            _s.OpenSettings("island");
            return;
        }
        body.OpenWidget(id, byUser: true);
    }

    // ---------- курсор ----------

    void Poll()
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4) /* PER_MONITOR_AWARE_V2 */);
        POINT last = default;
        bool lastNear = false, lastHeld = false, lastEsc = false;
        for (var tick = 0; ; tick++)
        {
            Thread.Sleep(16);
            if (!_running) continue;
            try
            {
                if (tick % 15 == 0 && _full.Update(_s.Monitors.All)) _s.Queue.TryEnqueue(OnFullChanged);
                if (!GetCursorPos(out var p)) continue;
                var held = (GetAsyncKeyState(0x01) | GetAsyncKeyState(0x02)) < 0;
                var esc = GetAsyncKeyState(0x1B) < 0;
                var near = false;
                foreach (var z in _zones)
                    if (p.X >= z.Left && p.X < z.Right && p.Y >= z.Top && p.Y < z.Bottom) { near = true; break; }
                var moved = p.X != last.X || p.Y != last.Y;
                var active = _active;
                // Нажатия и Escape нужны, только пока что-то раскрыто (клик снаружи закрывает).
                if ((near && moved) || near != lastNear || _timers || (active && (held != lastHeld || esc != lastEsc)) || (near && held != lastHeld))
                {
                    var (hp, hh, he) = (p, held, esc);
                    _s.Queue.TryEnqueue(() => Tick(hp, hh, he));
                }
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
        if (!_running) return;
        var pressed = held && !_lastHeld;
        var escDown = esc && !_lastEsc;
        _lastHeld = held;
        _lastEsc = esc;
        var now = Environment.TickCount64;
        var frame = false;
        try
        {
            foreach (var b in _bodies.Values.ToList()) frame |= b.Tick(p, held, pressed, escDown, now);
        }
        catch (Exception e) { Log.Error("остров: курсор", e); }
        _timers = _bodies.Values.Any(b => b.Waiting);
        if (frame) EnsureRendering();
    }

    void OnFullChanged()
    {
        var full = _full.Full;
        foreach (var (name, b) in _bodies) b.SetFull(full.Contains(name));
        Log.Write($"остров: fullscreen — {(full.Count == 0 ? "нигде" : string.Join(", ", full))}{(_full.Exclusive is { } x ? $", эксклюзив на {x}" : "")}");
    }

    /// Где курсор интересен опросу — снимок для фонового потока.
    public void PublishZones()
    {
        _zones = _bodies.Values.Select(b => b.Zone()).Where(z => z.Right > z.Left).ToArray();
        _active = _bodies.Values.Any(b => b.Mode != BodyMode.Rest);
    }

    // ---------- кадры ----------

    public void EnsureRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    void OnRendering(object? sender, object e)
    {
        try
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var dt = _lastFrame == 0 ? 1 / 60.0 : Math.Min(0.5, (now - _lastFrame) / (double)System.Diagnostics.Stopwatch.Frequency);
            _lastFrame = now;
            var any = false;
            foreach (var b in _bodies.Values.ToList()) any |= b.Frame(dt);
            if (!any) Stop();
        }
        catch (Exception ex)
        {
            Log.Error("остров: кадр", ex);
            Stop();
        }

        void Stop()
        {
            _rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    // ---------- события ----------

    /// Экран активного окна (наше окно впереди — экран курсора).
    string ActiveScreen()
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && ProcessId(fg) != (uint)Environment.ProcessId && GetWindowRect(fg, out var r))
            return _s.Monitors.At((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2).Name;
        GetCursorPos(out var p);
        return _s.Monitors.At(p.X, p.Y).Name;
    }

    string? Route(EventKind kind) =>
        EventRoute.Target(kind, ActiveScreen(), _full.Full.ToHashSet(), _full.Exclusive, _s.Monitors.All.Select(m => m.Name).ToList());

    /// Смена раскладки (и явная, и при переходе в окно с другим языком): только при фактической смене.
    void LanguageChanged()
    {
        var now = _s.Language.Current.Short;
        if (now == _lastLang) return;
        _lastLang = now;
        if (!_running || !_s.Config.Island.EventOn(EventKind.Language)) return;
        // Сменили раскладку в самом острове — пилюля не нужна.
        if (_bodies.Values.Any(b => b.Mode == BodyMode.Widget)) return;
        if (Route(EventKind.Language) is not { } screen || !_bodies.TryGetValue(screen, out var body)) return;
        var t = Text(now, 15, bold: true);
        t.CharacterSpacing = 40;
        body.ShowPill(new Border { Padding = new Thickness(18, 6, 18, 8), Child = t }, now, 1000, null);
    }

    /// Низкий заряд: пилюля вместо тоста Windows. false — остров выключен или событие выключено (будет тост).
    public bool ChargeAlert(ChargeDevice d, int percent, string text)
    {
        if (!_running || !_s.Config.Island.EventOn(EventKind.Charge)) return false;
        if (Route(EventKind.Charge) is not { } screen || !_bodies.TryGetValue(screen, out var body)) return false;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(16, 10, 18, 12) };
        row.Children.Add(Glyph(ChargeDevice.LevelGlyph(percent, false), 22, Amber));
        var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
        lines.Children.Add(Text($"{d.Name} · {percent} %", 14, bold: true));
        lines.Children.Add(Text(text, 12, AmberText));
        row.Children.Add(lines);
        body.ShowPill(row, $"{d.Name} · {percent} %", 4000, () => body.OpenWidget("charge", byUser: true));
        Log.Write($"остров: заряд «{d.Name} {percent} %» на {screen}");
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
}
