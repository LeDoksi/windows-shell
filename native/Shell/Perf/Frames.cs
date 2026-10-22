using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsShell.Perf;

/// Счёт кадров игры (design-v2 п. 8): события показа кадров Windows (ETW DxgKrnl/DXGI) читает
/// Intel PresentMon — тот же движок, что у FrameView и CapFrameX; в игру не встраиваемся.
/// Считаем только кадры, реально показанные на экране (DisplayedTime ≠ NA). Нужен админ или
/// группа «Пользователи журналов производительности» (PerfAccess).
public sealed class Frames : IDisposable
{
    const string Session = "WindowsShellFps";
    /// Сколько секунд кадров держим (1% low и график за минуту).
    const double KeepMs = 60_000;

    public static string Tool => Path.Combine(AppContext.BaseDirectory, "Tools", "PresentMon.exe");

    readonly object _lock = new();
    /// Показанные кадры: когда показан (мс QPC) и сколько был на экране (мс).
    readonly Queue<(double At, double Shown)> _frames = new();
    Process? _proc;
    int _pid;
    string _error = "";

    /// PresentMon не смог открыть сессию событий: нет прав.
    public bool AccessDenied { get; private set; }

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
            _frames.Clear();
            _chain = _chain.ContinueWith(_ => Apply(pid), TaskScheduler.Default);
        }
    }

    Task _chain = Task.CompletedTask;

    void Apply(int pid)
    {
        Process? old;
        lock (_lock)
        {
            if (pid != _pid) return;
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
        foreach (var a in new[] { "--process_id", pid.ToString(), "--output_stdout", "--no_console_stats", "--qpc_time_ms",
                     "--session_name", Session, "--stop_existing_session", "--terminate_on_proc_exit", "--no_track_input", "--no_track_gpu" })
            psi.ArgumentList.Add(a);
        try
        {
            var p = Process.Start(psi)!;
            Job.Add(p);
            string[]? header = null;
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                if (header is null) { header = line.Split(','); Log.Write($"FPS: PresentMon {pid}: {line[..Math.Min(line.Length, 300)]}"); return; }
                Row(p, header, line);
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
                if (pid != _pid) { Kill(p, stopSession: false); return; }
                _proc = p;
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Log.Write($"FPS: считаю кадры процесса {pid}");
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

    int _iTime = -1, _iShown = -1, _iLatency = -1, _iDropped = -1, _iPid = -1;
    string[]? _parsedHeader;

    void Row(Process p, string[] header, string line)
    {
        if (!ReferenceEquals(header, _parsedHeader))
        {
            _parsedHeader = header;
            int Col(params string[] names) => names.Select(n => Array.FindIndex(header, h => h.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
            _iTime = Col("CPUStartQPCTime", "CPUStartTime", "TimeInSeconds");
            // PresentMon 2.x: DisplayedTime (сколько кадр был на экране); 1.x: msBetweenDisplayChange + Dropped.
            _iShown = Col("DisplayedTime", "MsBetweenDisplayChange", "msBetweenDisplayChange");
            _iLatency = Col("DisplayLatency", "MsUntilDisplayed", "msUntilDisplayed");
            _iDropped = Col("Dropped");
            _iPid = Col("ProcessID");
        }
        var cells = line.Split(',');
        if (_iShown < 0 || _iShown >= cells.Length) return;
        if (_iDropped >= 0 && _iDropped < cells.Length && cells[_iDropped] == "1") return;
        if (!double.TryParse(cells[_iShown], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var shown) || shown <= 0) return;
        // Время показа: начало кадра + задержка до экрана (QPC в мс); нет — время прихода строки.
        var at = NowMs;
        if (_iTime >= 0 && _iTime < cells.Length && header[_iTime].Equals("CPUStartQPCTime", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(cells[_iTime], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var start))
        {
            at = start;
            if (_iLatency >= 0 && _iLatency < cells.Length && double.TryParse(cells[_iLatency], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) && lat > 0) at += lat;
        }
        lock (_lock)
        {
            if (!ReferenceEquals(p, _proc)) return;
            _frames.Enqueue((at, shown));
            while (_frames.Count > 0 && _frames.Peek().At < at - KeepMs) _frames.Dequeue();
        }
    }

    /// FPS сейчас: среднее за последние ~0,5 с показанных кадров; null — кадров нет (игра стоит
    /// или свернута, PresentMon ещё не начал).
    public double? Fps
    {
        get
        {
            lock (_lock)
            {
                if (_frames.Count == 0) return null;
                var arr = _frames.ToArray();
                var last = arr[^1];
                // Строки идут с задержкой: показ кадра известен только после показа следующего.
                if (NowMs - last.At > 1500) return null;
                double sum = 0;
                var n = 0;
                for (var i = arr.Length - 1; i >= 0 && sum < 500; i--) { sum += arr[i].Shown; n++; }
                return sum > 0 ? 1000.0 * n / sum : null;
            }
        }
    }

    /// 1% low за последнюю минуту: средний FPS худшего 1 % кадров.
    public double? Low1
    {
        get
        {
            lock (_lock)
            {
                if (_frames.Count < 100) return null;
                var times = _frames.Select(f => f.Shown).OrderByDescending(t => t).ToArray();
                var n = Math.Max(1, times.Length / 100);
                var avg = times.Take(n).Average();
                return avg > 0 ? 1000.0 / avg : null;
            }
        }
    }

    /// Последняя ошибка PresentMon (для проверки).
    public string Error { get { lock (_lock) return _error; } }

    static void Kill(Process p, bool stopSession)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
        p.Dispose();
        // Убитый PresentMon оставляет сессию событий открытой: закрываем её отдельно.
        if (stopSession) StopSession();
    }

    static void StopSession()
    {
        try
        {
            var psi = new ProcessStartInfo(Tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--session_name", Session, "--terminate_existing_session" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { }
    }

    public void Dispose() => Track(0);

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
