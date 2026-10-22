using System.Diagnostics;
using Microsoft.UI.Dispatching;
using WindowsShell.Apps;
using WindowsShell.Config;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Perf;

/// Что показывать сейчас: FPS игры (null — нет игры или кадров) и датчики.
public sealed record PerfNow(double? Fps, double? Low1, PerfSample? Sample, string? Game);

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
    Sensors? _sensors;
    bool _reading, _sensorsFailed;
    int _tick;
    string _hotkeyKey = "";
    PerfWindow? _window;

    /// Игра: процесс, окно, имя. Окно игры может быть не в фокусе (кликнули в окно монитора).
    int _gamePid;
    IntPtr _gameHwnd;
    string? _gameExe, _gameName;
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

    public PerfNow Now => new(Fps, Low1, Last, _gamePid != 0 ? _gameName : null);

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

    /// Пересчитать: какая игра, что запущено.
    void Evaluate()
    {
        var s = Settings;
        var fg = GetForegroundWindow();
        var root = fg == IntPtr.Zero ? IntPtr.Zero : GetAncestor(fg, GA_ROOTOWNER);
        var pid = root == IntPtr.Zero ? 0u : ProcessId(root);
        var own = pid == (uint)Environment.ProcessId;
        // Своё окно (монитор, Пуск, настройки) в фокусе — ничего не меняем: игра та же.
        var focused = own && _gameFocused;
        if (pid != 0 && !own)
        {
            var (exe, _) = ProcessInfo(pid);
            if (Games.Why(exe, s.Marks) is { } why)
            {
                focused = true;
                if (pid != _gamePid)
                {
                    _gamePid = (int)pid;
                    _gameExe = exe;
                    _gameName = AppName(exe!);
                    Log.Write($"игра в фокусе: {_gameName} ({Path.GetFileName(exe)}, {why})");
                    FpsHistory.Clear();
                }
                _gameHwnd = root;
            }
        }
        if (_gamePid != 0 && !IsWindow(_gameHwnd))
        {
            Log.Write($"игра закрыта: {_gameName}");
            _gamePid = 0;
            _gameHwnd = IntPtr.Zero;
            _gameExe = _gameName = null;
            Fps = Low1 = null;
            focused = false;
        }
        _gameFocused = focused;
        RegisterHotkey(s);

        var overlay = s.Overlay && focused;
        var window = _window != null;
        // FPS: игра в фокусе и оверлей его показывает, или открыто окно, а игра ещё идёт.
        var wantFps = _gamePid != 0 && ((overlay && s.Items.Any(i => i is "fps" or "frametime")) || window);
        _frames.Track(wantFps ? _gamePid : 0);
        if (!wantFps) Fps = Low1 = null;
        var run = overlay || window || Previewing;
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
        if (_gamePid != 0 && !IsWindow(_gameHwnd)) { Evaluate(); if (!_timer.IsRunning) return; }
        Fps = _frames.Fps;
        // 1% low нужен только окну, считается сортировкой — раз в секунду.
        if (_tick % 2 == 0)
        {
            Low1 = _window != null ? _frames.Low1 : null;
            if (_window != null)
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
            try { _overlay.Show(s, Now with { Fps = Fps ?? 144 }, MonitorInfo(mon).rcMonitor, MonitorScale(mon)); }
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

    /// Показать оверлей на 5 секунд (кнопка «Показать» в настройках).
    public void Preview()
    {
        _previewUntil = Environment.TickCount64 + 5000;
        Evaluate();
    }

    bool NeedSensors()
    {
        if (_window != null || Previewing) return true;
        return Settings.Overlay && _gameFocused && Settings.Items.Any(i => i is not ("fps" or "frametime"));
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
