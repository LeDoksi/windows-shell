using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsShell.Island.Core;

namespace WindowsShell.Perf;

/// Счёт кадров игры (design-v2 п. 8, И8): события показа кадров Windows (ETW DxgKrnl/DXGI) читает
/// Intel PresentMon — тот же движок, что у FrameView и CapFrameX; в игру не встраиваемся.
/// Считаем кадры, реально показанные на экране (DisplayedTime ≠ NA); если показ не виден —
/// время между кадрами самой программы (FrameStats). Нет ни одной строки за 4 с — «широкий»
/// режим (все процессы, строки только этого pid, в логе — кто рисует); и там пусто — NoData.
/// Нужен админ или группа «Пользователи журналов производительности» (PerfAccess).
public sealed class Frames : IDisposable
{
    public enum Status { Idle, Waiting, Ok, NoData, Denied }

    const string DefaultSession = "WindowsShellFps";
    /// Сколько ждать первой строки, прежде чем расширить поиск или сказать «нет данных».
    const long FirstRowMs = 4000;
    readonly string _session;
    readonly Timer _watch;

    /// `session` — имя сессии событий; у пробы своё, чтобы не остановить счёт кадров оболочки.
    public Frames(string session = DefaultSession)
    {
        _session = session;
        _watch = new Timer(_ => Watch(), null, 1000, 1000);
    }

    public static string Tool => Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon.exe");

    readonly object _lock = new();
    readonly FrameStats _stats = new();
    /// Широкий режим: сколько строк от каждой программы (для лога).
    readonly Dictionary<string, int> _presenters = new(StringComparer.OrdinalIgnoreCase);
    Process? _proc;
    int _pid;
    bool _wide, _rows;
    long _startedAt, _presentersAt;
    Status _status = Status.Idle;
    string _error = "";

    /// PresentMon не смог открыть сессию событий: нет прав.
    public bool AccessDenied { get; private set; }

    /// Что с кадрами сейчас.
    public Status State { get { lock (_lock) return AccessDenied && !_rows ? Status.Denied : _status; } }

    /// Идёт поиск кадров по всем программам (у своего pid строк не было).
    public bool Wide { get { lock (_lock) return _wide; } }

    /// Процесс, кадры которого считаем (0 — никакой).
    public int Pid { get { lock (_lock) return _pid; } }

    public static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// Считать кадры процесса `pid` (тот же — ничего не делаем; 0 — остановить). Запуск и остановка
    /// PresentMon идут по очереди в фоне, вызывать можно из любого потока.
    public void Track(int pid)
    {
        lock (_lock)
        {
            if (pid == _pid) return;
            _pid = pid;
            Reset(pid == 0 ? Status.Idle : Status.Waiting);
            _wide = false;
            _chain = _chain.ContinueWith(_ => Apply(pid, wide: false), TaskScheduler.Default);
        }
    }

    void Reset(Status status)
    {
        _stats.Clear();
        _presenters.Clear();
        _rows = false;
        _status = status;
        _startedAt = Environment.TickCount64;
        _presentersAt = _startedAt;
    }

    Task _chain = Task.CompletedTask;

    /// Раз в секунду: строк нет 4 с — расширить поиск, потом сказать «нет данных».
    void Watch()
    {
        try
        {
            lock (_lock)
            {
                if (_pid == 0 || _rows || AccessDenied) return;
                var now = Environment.TickCount64;
                if (now - _startedAt < FirstRowMs) return;
                if (!_wide)
                {
                    _wide = true;
                    _startedAt = now;
                    _presentersAt = now;
                    var pid = _pid;
                    Log.Write($"FPS: у процесса {pid} нет кадров за {FirstRowMs / 1000} с — ищу по всем программам");
                    _chain = _chain.ContinueWith(_ => Apply(pid, wide: true), TaskScheduler.Default);
                    return;
                }
                if (_status == Status.Waiting)
                {
                    _status = Status.NoData;
                    Log.Write($"FPS: процесс {_pid} кадров не отдаёт — «нет данных»; {Presenters()}");
                }
                else if (now - _presentersAt > 10_000)
                {
                    _presentersAt = now;
                    Log.Write($"FPS: широкий режим, {Presenters()}");
                }
            }
        }
        catch { }
    }

    string Presenters() => _presenters.Count == 0 ? "строк PresentMon нет совсем"
        : "рисуют: " + string.Join(", ", _presenters.OrderByDescending(p => p.Value).Take(5).Select(p => $"{p.Key} {p.Value}"));

    void Apply(int pid, bool wide)
    {
        Process? old;
        lock (_lock)
        {
            if (pid != _pid || wide != _wide) return;
            old = _proc;
            _proc = null;
        }
        if (old != null) Kill(old, stopSession: pid == 0);
        if (pid == 0) return;
        if (!File.Exists(Tool))
        {
            Log.Write($"FPS: нет {Tool}");
            return;
        }
        var psi = new ProcessStartInfo(Tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var args = new List<string>();
        if (!wide) args.AddRange(["--process_id", pid.ToString(), "--terminate_on_proc_exit"]);
        args.AddRange(["--output_stdout", "--no_console_stats", "--qpc_time_ms", "--session_name", _session, "--stop_existing_session", "--no_track_input", "--no_track_gpu"]);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            var p = Process.Start(psi)!;
            Job.Add(p);
            FrameColumns? cols = null;
            var appCol = -1;
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                if (cols is null)
                {
                    var header = line.Split(',');
                    cols = FrameColumns.Pick(header);
                    appCol = Array.FindIndex(header, h => h.Trim().Equals("Application", StringComparison.OrdinalIgnoreCase));
                    Log.Write($"FPS: PresentMon {(wide ? "все программы" : pid.ToString())}: {line[..Math.Min(line.Length, 300)]}");
                    return;
                }
                Row(p, cols, appCol, line);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                lock (_lock) if (_error.Length < 2000) _error += e.Data + "\n";
                if (e.Data.Contains("access denied", StringComparison.OrdinalIgnoreCase) || e.Data.Contains("Performance Log Users", StringComparison.OrdinalIgnoreCase))
                    AccessDenied = true;
            };
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                string err;
                lock (_lock) err = _error.Trim();
                Log.Write($"FPS: PresentMon вышел ({SafeExit(p)}){(err.Length > 0 ? ": " + err.Replace('\n', ' ') : "")}");
            };
            lock (_lock)
            {
                _error = "";
                if (pid != _pid || wide != _wide) { Kill(p, stopSession: false); return; }
                _proc = p;
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Log.Write($"FPS: считаю кадры процесса {pid}{(wide ? " (по всем программам)" : "")}");
        }
        catch (Exception e)
        {
            Log.Error("FPS: PresentMon", e);
        }
    }

    static string SafeExit(Process p)
    {
        try { return $"код {p.ExitCode}"; } catch { return "?"; }
    }

    static bool Num(string[] cells, int i, out double v)
    {
        v = 0;
        return i >= 0 && i < cells.Length && double.TryParse(cells[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
    }

    void Row(Process p, FrameColumns c, int appCol, string line)
    {
        var cells = line.Split(',');
        lock (_lock)
        {
            if (!ReferenceEquals(p, _proc)) return;
            if (_wide)
            {
                var app = appCol >= 0 && appCol < cells.Length ? cells[appCol] : "?";
                _presenters[app] = _presenters.GetValueOrDefault(app) + 1;
                if (!Num(cells, c.Pid, out var rowPid) || (int)rowPid != _pid) return;
            }
            // Время показа: начало кадра + задержка до экрана (QPC в мс); нет — время прихода строки.
            var at = NowMs;
            if (c.QpcTime && Num(cells, c.Time, out var start))
            {
                at = start;
                if (Num(cells, c.Latency, out var lat) && lat > 0) at += lat;
            }
            var dropped = c.Dropped >= 0 && c.Dropped < cells.Length && cells[c.Dropped] == "1";
            if (!dropped && Num(cells, c.Shown, out var shown) && shown > 0) _stats.Add(at, shown);
            else if (Num(cells, c.Presented, out var frame) && frame > 0) _stats.AddPresented(at, frame);
            else return;
            if (!_rows)
            {
                _rows = true;
                Log.Write($"FPS: первые кадры процесса {_pid}{(_wide ? " (найдены в широком режиме)" : "")}{(_stats.PresentedOnly ? ", показ не виден — считаю кадры программы" : "")}");
            }
            _status = Status.Ok;
        }
    }

    /// FPS сейчас: среднее за последние ~0,5 с; null — кадров нет (игра стоит или свернута,
    /// PresentMon ещё не начал).
    public double? Fps { get { lock (_lock) return _stats.Fps(NowMs); } }

    /// 1% low за последнюю минуту; null — кадров меньше сотни.
    public double? Low1 { get { lock (_lock) return _stats.Low1; } }

    /// Время кадра сейчас, мс.
    public double? FrameMs { get { lock (_lock) return _stats.FrameMs(NowMs); } }

    /// Последние n времён кадра (мс) для графика.
    public double[] Recent(int n) { lock (_lock) return _stats.Recent(n); }

    /// Последняя ошибка PresentMon (для проверки).
    public string Error { get { lock (_lock) return _error; } }

    void Kill(Process p, bool stopSession)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
        p.Dispose();
        // Убитый PresentMon оставляет сессию событий открытой: закрываем её отдельно.
        if (stopSession) StopSession();
    }

    void StopSession()
    {
        try
        {
            var psi = new ProcessStartInfo(Tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--session_name", _session, "--terminate_existing_session" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { }
    }

    public void Dispose()
    {
        Track(0);
        _watch.Dispose();
    }

    /// Задание Windows «убить вместе с оболочкой»: PresentMon не переживёт её сбой.
    static class Job
    {
        static IntPtr _job;

        public static void Add(Process p)
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObjectW(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(_job, 9 /* ExtendedLimitInformation */, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }
            AssignProcessToJobObject(_job, p.Handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr attr, string? name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
