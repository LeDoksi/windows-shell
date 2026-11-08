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
    readonly RecordRoute _recRoute = new();
    string? _badgeOn;
    bool _badgeAway, _badgePaused;
    TextBlock? _badgeTime;

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
        _s.Shots.Changed += () => { Indicators(); UpdateRecording(); };
        _s.Shots.Tick += () => { Indicators(); UpdateRecording(); };
        _s.Shots.IslandSaved = ShotSaved;
        _s.Bin.Changed += Indicators;
        _s.Shelf.Changed += Indicators;
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
            UpdateRecording();
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
            // Пилюля записи могла быть на пропавшем мониторе.
            _badgeOn = null;
            UpdateRecording();
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
        // ⚙ — настройки-«лист» внутри острова (П14).
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
        UpdateRecording();
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

    // ---------- запись и снимки (волна 2) ----------

    int ScreenNumber(string name)
    {
        var all = _s.Monitors.All;
        for (var i = 0; i < all.Count; i++) if (all[i].Name == name) return i + 1;
        return 0;
    }

    /// Пилюля идущей записи (решение E22): на записываемом экране, в эксклюзиве — на соседнем
    /// с подписью и стрелкой. Привязана к самой записи, не к тому, видна ли игра.
    void UpdateRecording()
    {
        if (!_running) return;
        try
        {
            var shots = _s.Shots;
            if (!shots.Recording || shots.RecordingScreen is not { } recorded)
            {
                if (_badgeOn != null) Log.Write("остров: запись — пилюля убрана");
                foreach (var b in _bodies.Values) b.SetBadge(null, null);
                _recRoute.Reset();
                _badgeOn = null;
                _badgeTime = null;
                return;
            }
            var screens = _s.Monitors.All.Select(m => m.Name).ToList();
            var target = _recRoute.Step(Environment.TickCount64, recorded, _full.Exclusive, _full.Full.ToHashSet(), screens);
            if (target is null || !_bodies.TryGetValue(target, out var body)) return;
            var away = _recRoute.Away;
            var paused = shots.RecordingPaused;
            if (target != _badgeOn || away != _badgeAway || paused != _badgePaused || _badgeTime is null)
            {
                foreach (var (name, b) in _bodies) if (name != target) b.SetBadge(null, null);
                Log.Write($"остров: запись экрана {recorded} — пилюля на {target}{(away ? " (эксклюзив, со стрелкой)" : "")}");
                _badgeOn = target;
                _badgeAway = away;
                _badgePaused = paused;
                body.SetBadge(RecordBadge(target, recorded, away, paused), () => body.OpenWidget("shots", byUser: true));
            }
            _badgeTime!.Text = Shots.ShotsService.Length(shots.RecordingTime);
            body.SetBadge(body.BadgeContent, () => body.OpenWidget("shots", byUser: true));
        }
        catch (Exception e) { Log.Error("остров: пилюля записи", e); }
    }

    UIElement RecordBadge(string on, string recorded, bool away, bool paused)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(14, 5, 14, 7) };
        row.Children.Add(Text(paused ? "❚❚" : "●", 13, paused ? Amber : Red, bold: true));
        _badgeTime = Text("0:00", 13, paused ? Amber : Red, bold: true);
        row.Children.Add(_badgeTime);
        if (away)
        {
            row.Children.Add(Text($"· пишется экран {ScreenNumber(recorded)}", 13, Ink));
            var from = _s.Monitors.ByName(on);
            var to = _s.Monitors.ByName(recorded);
            if (from != null && to != null)
            {
                var dir = RecordRoute.Direction(new RectI(from.Bounds.Left, from.Bounds.Top, from.Bounds.Right, from.Bounds.Bottom),
                    new RectI(to.Bounds.Left, to.Bounds.Top, to.Bounds.Right, to.Bounds.Bottom));
                row.Children.Add(Glyph(dir switch { Arrow.Left => "\uE72B", Arrow.Right => "\uE72A", Arrow.Up => "\uE74A", _ => "\uE74B" }, 14, Ink));
            }
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, away ? $"Идёт запись экрана {ScreenNumber(recorded)}" : "Идёт запись экрана");
        return row;
    }

    /// Результат снимка (пилюля, эскиз Events). true — остров взял событие на себя.
    bool ShotSaved(Shots.ShotItem item, string caption)
    {
        if (!_running || !_s.Config.Island.EventOn(EventKind.Shot)) return false;
        if (Route(EventKind.Shot) is { } screen && _bodies.TryGetValue(screen, out var body))
        {
            body.ShowPill(ShotPill(item, caption, body), caption, 4500, () => body.OpenWidget("shots", byUser: true));
            Log.Write($"остров: {caption} на {screen}");
        }
        else Log.Write($"остров: {caption} — тихо, в галерее (игра на весь экран)");
        return true;
    }

    UIElement ShotPill(Shots.ShotItem item, string caption, IslandBody body)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Padding = new Thickness(12, 10, 12, 12) };
        var image = new Image { Stretch = Stretch.UniformToFill };
        var thumb = new Border { Width = 72, Height = 44, CornerRadius = new CornerRadius(8), Background = B(Group), Child = image };
        _ = LoadThumb(item, image);
        // Миниатюру можно сразу перетащить в чат или папку.
        Draggable(thumb, () => item.Path, () => { body.Sleep(restoreFocus: false); _s.Shots.Edit(item); }, () => body.Sleep(restoreFocus: false));
        ToolTipService.SetToolTip(thumb, "Перетащи в любое окно");
        row.Children.Add(thumb);
        var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260 };
        lines.Children.Add(Text(item.IsImage ? "Снимок сохранён" : caption, 14, bold: true));
        lines.Children.Add(Text(item.IsImage ? "и скопирован в буфер" : "в «Видео\\Записи экрана»", 12, Sub));
        row.Children.Add(lines);
        var edit = Btn(item.IsImage ? "Изменить" : "Открыть", click: () => { body.Sleep(restoreFocus: false); _s.Shots.Edit(item); });
        edit.PointerPressed += (_, e) => e.Handled = true;
        row.Children.Add(edit);
        return row;
    }

    static async Task LoadThumb(Shots.ShotItem item, Image image)
    {
        try { image.Source = await Shots.ShotImages.Load(item, 144); }
        catch { }
    }

    /// Бросили файлы в режиме приёма (П2).
    public void Dropped(IslandBody body, DropTarget target, List<string> paths)
    {
        var n = paths.Count;
        string text;
        if (target == DropTarget.Bin)
        {
            _s.Bin.Delete(paths);
            text = $"В корзине · {Tail.RecycleBin.Objects(n)}";
        }
        else
        {
            var added = _s.Shelf.Add(paths);
            text = added == n ? $"На полке · +{n}" : $"На полке · +{added} (остальное уже там)";
        }
        Log.Write($"остров: бросили {n} → {(target == DropTarget.Bin ? "корзина" : "полка")}");
        var widget = target == DropTarget.Bin ? "bin" : "shelf";
        if (body.Mode == BodyMode.Widget && body.Widget?.Id == widget) return; // виджет и так открыт — он обновится сам
        body.Sleep(restoreFocus: false);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(16, 8, 18, 10) };
        row.Children.Add(Glyph(target == DropTarget.Bin ? "\uE74D" : "\uE7B8", 18));
        row.Children.Add(Text(text, 14, bold: true));
        body.ShowPill(row, text, 2200, () => body.OpenWidget(widget, byUser: true));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
}
