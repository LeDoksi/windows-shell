using System.Diagnostics;
using Microsoft.UI.Dispatching;
using WindowsShell.Apps;
using WindowsShell.Config;
using WindowsShell.Island.Core;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Perf;

/// Что с FPS: игры нет; ждём кадров; есть; игра кадров не отдаёт; нет разрешения.
public enum FpsState { NoGame, Waiting, Ok, NoData, Denied }

/// Что показывать сейчас: FPS игры (null — нет игры или кадров), 1% low, время кадра,
/// последние кадры для графика и датчики.
public sealed record PerfNow(double? Fps, double? Low1, PerfSample? Sample, string? Game, double? FrameMs = null, double[]? Recent = null, FpsState State = FpsState.NoGame);

/// Виджет «Монитор ПК» (design-v2 п. 8): следит, в фокусе ли игра, считает её кадры, читает
/// датчики, показывает оверлей и окно. Работает, только когда нужно: без игры в фокусе и без
/// окна — ничего не читает (только слушает смену активного окна).
public sealed class PerfService
{
    /// Сколько секунд истории в окне (графики за последнюю минуту).
    public const int HistorySeconds = 60;

    readonly DispatcherQueue _queue;
    readonly ShellConfig _config;
    readonly WindowTracker _tracker;
    readonly Hotkeys _hotkeys;
    readonly DispatcherQueueTimer _timer;
    readonly Frames _frames = new();
    readonly Overlay _overlay = new();
    readonly GameProbe _probe = new();
    /// Кто сейчас смотрит на цифры (виджет острова, строка закрепления): датчики и FPS нужны.
    readonly HashSet<object> _holders = [];
    /// Программа впереди на весь экран, про которую проба ещё решает, игра ли это (И8).
    (int Pid, IntPtr Hwnd, string Exe)? _candidate;
    int _probeTrack;
    /// Последняя программа впереди (не оболочка): для кнопок «Это игра» / «Это не игра».
    (string Exe, string Name)? _lastApp;
    Sensors? _sensors;
    bool _reading, _sensorsFailed;
    int _tick;
    string _hotkeyKey = "";
    PerfWindow? _window;

    /// Игра: процесс, окно, имя. Окно игры может быть не в фокусе (кликнули в окно монитора).
    int _gamePid;
    IntPtr _gameHwnd;
    string? _gameExe, _gameName, _gameWhy;
    bool _gameFocused;
    /// «Показать» в настройках: оверлей на мониторе с курсором до этого времени.
    long _previewUntil;

    public PerfSettings Settings => new(_config.Widget("perf"));

    public PerfSample? Last { get; private set; }
    public double? Fps { get; private set; }
    public double? Low1 { get; private set; }

    /// История за минуту (раз в секунду), самое старое первым.
    public List<PerfSample> History { get; } = [];
    public List<double?> FpsHistory { get; } = [];

    public PerfNow Now => new(Fps, Low1, Last, _gamePid != 0 ? _gameName : null, _gamePid != 0 ? _frames.FrameMs : null,
        _gamePid != 0 && Settings.Items.Contains("graph") ? _frames.Recent(40) : null, FpsState);

    /// Что с FPS сейчас (виджет и оверлей пишут «нет данных», а не пустоту).
    public FpsState FpsState
    {
        get
        {
            if (_gamePid == 0) return FpsState.NoGame;
            if (PerfAccess.Current == PerfAccess.State.Needed) return FpsState.Denied;
            return _frames.State switch
            {
                Frames.Status.Denied => FpsState.Denied,
                Frames.Status.NoData => FpsState.NoData,
                Frames.Status.Ok => Fps is null ? FpsState.Waiting : FpsState.Ok,
                _ => FpsState.Waiting,
            };
        }
    }

    /// Игра сейчас: имя, exe и почему считается игрой.
    public (string Name, string Exe, string Why)? Game => _gamePid != 0 && _gameExe != null ? (_gameName ?? "", _gameExe, _gameWhy ?? "") : null;

    /// Последняя программа впереди, если она не игра (для «Это игра»).
    public (string Exe, string Name)? LastApp => _lastApp is { } a && (Game is not { } g || !g.Exe.Equals(a.Exe, StringComparison.OrdinalIgnoreCase)) ? a : null;

    /// Кто-то смотрит на цифры (виджет острова, строка закрепления): держать датчики и FPS.
    public void Hold(object who, bool on)
    {
        var changed = on ? _holders.Add(who) : _holders.Remove(who);
        if (changed) Evaluate();
    }

    bool Held => _holders.Count > 0;

    public string GpuName => _sensors?.GpuName ?? "";
    public string CpuTempSource => _sensors?.CpuTempSource ?? "нет";

    /// FPS не считается: нет прав на события Windows (PerfAccess).
    public bool FpsDenied => _frames.AccessDenied;

    /// Новые цифры (поток интерфейса, раз в 0,5 с, пока что-то показано).
    public event Action? Updated;

    /// Открыть настройки виджета (правый клик по окну).
    public Action OpenSettings { get; set; } = () => { };

    /// Показать меню оболочки в точке экрана (правый клик по окну).
    public Action<int, int, List<Ui.MenuItem>> ShowMenu { get; set; } = (_, _, _) => { };

    public PerfService(DispatcherQueue queue, ShellConfig config, WindowTracker tracker, MessageWindow messages)
    {
        _queue = queue;
        _config = config;
        _tracker = tracker;
        _hotkeys = new Hotkeys(messages, firstId: 2000);
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Tick();
        tracker.ForegroundChanged += Evaluate;
        messages.Resumed += Evaluate;
    }

    /// Монитор с игрой в фокусе (предупреждения о заряде показываются там же), null — игры нет.
    public RECT? GameScreen => _gameFocused && IsWindow(_gameHwnd) ? MonitorInfo(MonitorFromWindow(_gameHwnd, 2)).rcMonitor : null;

    /// Зовётся при каждой смене конфига и при запуске.
    public void Apply()
    {
        var s = Settings;
        if (s.Window && _window is null)
        {
            _window = new PerfWindow(this, _config);
            _window.Closed += () => _window = null;
        }
        else if (!s.Window && _window is { } w)
        {
            _window = null;
            w.Close();
        }
        _window?.Apply(s);
        Evaluate();
    }

    /// Fullscreen где-то поменялся (окно впереди развернулось на весь экран): пересчитать.
    public void Reevaluate() => Evaluate();

    /// Пересчитать: какая игра, что запущено.
    void Evaluate()
    {
        var s = Settings;
        var marks = s.Marks;
        var fg = GetForegroundWindow();
        var root = fg == IntPtr.Zero ? IntPtr.Zero : GetAncestor(fg, GA_ROOTOWNER);
        var pid = root == IntPtr.Zero ? 0u : ProcessId(root);
        var own = pid == (uint)Environment.ProcessId;
        // Своё окно (монитор, Пуск, настройки, остров) в фокусе — ничего не меняем: игра та же.
        var focused = own && _gameFocused;
        if (pid != 0 && !own)
        {
            var (exe, _) = ProcessInfo(pid);
            var why = Games.Why(exe, marks);
            if (why is null && exe != null && _probe.Known((int)pid) == true && !marks.ContainsKey(exe)) why = "весь экран + рисует кадры";
            if (exe != null && ClassName(root) is not ("Progman" or "WorkerW" or "Shell_TrayWnd"))
                _lastApp = (exe, AppName(exe));
            if (why is { } w)
            {
                _candidate = null;
                focused = true;
                if (pid != _gamePid) SetGame((int)pid, exe!, w);
                _gameHwnd = root;
            }
            else if (exe != null && !marks.ContainsKey(exe) && !Games.InNever(exe))
            {
                // Не отмечена и не из лаунчера: решит проба (весь экран + кадры).
                if (_candidate?.Pid != (int)pid) _candidate = ((int)pid, root, exe);
            }
            else _candidate = null;
        }
        // Отметили «это не игра», пока она шла.
        if (_gamePid != 0 && _gameExe != null && marks.TryGetValue(_gameExe, out var marked) && !marked)
        {
            Log.Write($"игра снята вручную: {_gameName}");
            ClearGame();
            focused = false;
        }
        if (_gamePid != 0 && !IsWindow(_gameHwnd))
        {
            Log.Write($"игра закрыта: {_gameName}");
            _probe.Forget(_gamePid);
            ClearGame();
            focused = false;
        }
        _gameFocused = focused;
        RegisterHotkey(s);
        ProbeStep();
        focused = _gameFocused;

        var overlay = s.Overlay && focused;
        var window = _window != null;
        // FPS: игра в фокусе и оверлей его показывает, или открыто окно / виджет, а игра ещё идёт.
        var wantFps = _gamePid != 0 && ((overlay && s.Items.Any(i => i is "fps" or "low1" or "frametime" or "graph")) || window || Held);
        _frames.Track(wantFps ? _gamePid : _probeTrack);
        if (!wantFps) Fps = Low1 = null;
        var run = overlay || window || Previewing || Held || _probeTrack != 0;
        if (run && !_timer.IsRunning)
        {
            _timer.Start();
            Tick();
        }
        else if (!run && _timer.IsRunning)
        {
            _timer.Stop();
            _overlay.Hide();
            // Датчики не держим: их запрос к счётчикам Windows стоит памяти.
            _sensors?.Dispose();
            _sensors = null;
            History.Clear();
            Last = null;
        }
        if (!overlay) _overlay.Hide();
    }

    void SetGame(int pid, string exe, string why)
    {
        _gamePid = pid;
        _gameExe = exe;
        _gameName = AppName(exe);
        _gameWhy = why;
        Log.Write($"игра в фокусе: {_gameName} ({Path.GetFileName(exe)}, {why})");
        FpsHistory.Clear();
    }

    void ClearGame()
    {
        _gamePid = 0;
        _gameHwnd = IntPtr.Zero;
        _gameExe = _gameName = _gameWhy = null;
        Fps = Low1 = null;
    }

    /// Проба И8: программа впереди на весь экран, не отмечена и не из лаунчера — 4 с считаем её
    /// кадры; были — игра, не было — не игра (до выхода процесса).
    void ProbeStep()
    {
        _probeTrack = 0;
        if (_candidate is not { } c) return;
        if (!IsWindow(c.Hwnd)) { _probe.Forget(c.Pid); _candidate = null; return; }
        // Без прав на кадры решить нельзя — не объявляем «не игра» зря.
        if (_frames.AccessDenied || PerfAccess.Current == PerfAccess.State.Needed) return;
        var fg = GetForegroundWindow();
        var front = fg != IntPtr.Zero && GetAncestor(fg, GA_ROOTOWNER) == c.Hwnd;
        var full = front && IsFullscreen(c.Hwnd);
        var frames = _frames.Pid == c.Pid && _frames.Fps != null;
        var d = _probe.Step(Environment.TickCount64, c.Pid, full, candidate: true, frames);
        if (d.Game == true)
        {
            _candidate = null;
            SetGame(c.Pid, c.Exe, "весь экран + рисует кадры");
            _gameHwnd = c.Hwnd;
            _gameFocused = front;
            RegisterHotkey(Settings);
        }
        else if (d.Game == false)
        {
            _candidate = null;
            Log.Write($"не игра: {AppName(c.Exe)} ({Path.GetFileName(c.Exe)}) — на весь экран, но кадров нет 4 с");
        }
        else _probeTrack = d.Track;
        if (d.Track != 0 && _probe.Probing == c.Pid && d.Game is null && _frames.Pid != c.Pid)
            Log.Write($"проба игры: {AppName(c.Exe)} на весь экран — считаю кадры");
    }

    /// Окно на весь экран своего монитора и без рамки (то же правило, что у острова и Bars).
    static bool IsFullscreen(IntPtr h)
    {
        if (!GetWindowRect(h, out var r)) return false;
        var s = MonitorInfo(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST)).rcMonitor;
        SHQueryUserNotificationState(out var state);
        var covers = r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom;
        var resizable = ((long)GetWindowLongPtrW(h, GWL_STYLE) & 0x00040000 /* WS_THICKFRAME */) != 0;
        return FullscreenRule.IsFull(state is 3 or 4, covers, IsZoomed(h), resizable);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    static string AppName(string exe)
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }

    void RegisterHotkey(PerfSettings s)
    {
        // Клавиша действует только в игре: в остальных программах сочетание свободно.
        var key = _gameFocused && s.Hotkey is { } h ? h : "";
        if (key == _hotkeyKey) return;
        _hotkeyKey = key;
        _hotkeys.Set(key.Length > 0 ? [(key, ToggleOverlay)] : []);
    }

    void ToggleOverlay()
    {
        var on = !Settings.Overlay;
        Log.Write($"оверлей {(on ? "включён" : "выключен")} клавишей");
        _config.Update(_ => Settings.Overlay = on);
    }

    void Tick()
    {
        _tick++;
        if ((_gamePid != 0 && !IsWindow(_gameHwnd)) || _probeTrack != 0) { Evaluate(); if (!_timer.IsRunning) return; }
        Fps = _gamePid != 0 ? _frames.Fps : null;
        // 1% low считается сортировкой — раз в секунду, пока считаются кадры игры.
        if (_tick % 2 == 0)
        {
            Low1 = _gamePid != 0 && _frames.Pid == _gamePid ? _frames.Low1 : null;
            if (_window != null || Held)
            {
                FpsHistory.Add(_gamePid != 0 ? Fps : null);
                while (FpsHistory.Count > HistorySeconds) FpsHistory.RemoveAt(0);
            }
            if (NeedSensors()) ReadSensors();
        }
        var s = Settings;
        if (s.Overlay && _gameFocused && IsWindow(_gameHwnd))
        {
            var mon = MonitorFromWindow(_gameHwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            try { _overlay.Show(s, Now, MonitorInfo(mon).rcMonitor, MonitorScale(mon)); }
            catch (Exception e) { Log.Error("оверлей", e); }
        }
        else if (Previewing)
        {
            GetCursorPos(out var p);
            var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            var demo = Now with { Fps = Fps ?? 144, Low1 = Low1 ?? 98, FrameMs = 1000 / (Fps ?? 144), State = FpsState.Ok, Recent = DemoFrames };
            try { _overlay.Show(s, demo, MonitorInfo(mon).rcMonitor, MonitorScale(mon)); }
            catch (Exception e) { Log.Error("оверлей", e); }
        }
        else
        {
            _overlay.Hide();
            if (_previewUntil != 0) { _previewUntil = 0; Evaluate(); }
        }
        Updated?.Invoke();
    }

    bool Previewing => Environment.TickCount64 < _previewUntil;

    /// Пример графика для «Показать»: ровно и один заметный рывок.
    static readonly double[] DemoFrames = Enumerable.Range(0, 40).Select(i => i == 27 ? 21.0 : 6.9 + (i % 5) * 0.2).ToArray();

    /// Показать оверлей на 5 секунд (кнопка «Показать» в настройках).
    public void Preview()
    {
        _previewUntil = Environment.TickCount64 + 5000;
        Evaluate();
    }

    bool NeedSensors()
    {
        if (_window != null || Previewing || Held) return true;
        return Settings.Overlay && _gameFocused && Settings.Items.Any(i => i is not ("fps" or "low1" or "frametime" or "graph"));
    }

    void ReadSensors()
    {
        if (_reading || _sensorsFailed) return;
        _reading = true;
        var sensors = _sensors;
        Task.Run(() =>
        {
            PerfSample? sample = null;
            try
            {
                sensors ??= new Sensors();
                sample = sensors.Read();
            }
            catch (Exception e)
            {
                Log.Error("датчики", e);
                _queue.TryEnqueue(() => _sensorsFailed = true);
            }
            _queue.TryEnqueue(() =>
            {
                _reading = false;
                if (!_timer.IsRunning) { if (!ReferenceEquals(sensors, _sensors)) sensors?.Dispose(); return; }
                _sensors = sensors;
                if (sample is null) return;
                Last = sample;
                History.Add(sample);
                while (History.Count > HistorySeconds) History.RemoveAt(0);
            });
        });
    }

    /// Пометить программу «это игра» / «не игра» / снять отметку (null).
    public void Mark(string exe, bool? game)
    {
        Log.Write($"монитор ПК: {Path.GetFileName(exe)} отмечена {(game switch { true => "«игра»", false => "«не игра»", _ => "без отметки" })}");
        _config.Update(_ => Settings.Mark(exe, game));
    }

    /// Для меню «Отметить»: открытые сейчас программы (exe, имя).
    public List<(string Exe, string Name)> RunningApps() =>
        _tracker.Windows.Where(w => w.Exe != null)
            .GroupBy(w => w.Exe!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First().AppName))
            .OrderBy(x => x.AppName)
            .ToList();

    /// Почему программа считается игрой (для настроек), null — не считается.
    public string? Why(string exe) => Games.Why(exe, Settings.Marks);

    public void Shutdown()
    {
        _frames.Dispose();
        _overlay.Dispose();
    }
}
