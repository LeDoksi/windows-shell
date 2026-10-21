using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsShell.Native;

/// Сторож (WSH-101, design-v2 п. 12): тот же exe с `--watchdog <pid>`, без окон на экране.
/// Работает всё время, пока работает оболочка, и поднимает её, если она упала или зависла.
/// - Упала (закрылась не через «Выход», перезапуск или выключение ПК): через 2 с запускает снова.
/// - Зависла: раз в 5 с спрашивает окно сообщений оболочки (как Windows решает «Не отвечает»);
///   не отвечает ~15 с — пишет отчёт, где зависла, закрывает и запускает снова. После сна 30 с не проверяет.
/// - 3 сбоя за 5 минут: больше не поднимает, возвращает панель задач Windows, уведомление
///   с кнопкой «Запустить ещё раз». Сам остаётся ждать кнопку, ярлык или Ctrl+Alt+Shift+R.
/// - Ctrl+Alt+Shift+R держит сторож: работает, даже когда оболочка зависла или закрыта.
/// Оболочка и сторож говорят сообщениями окнам друг друга (классы окон известны заранее).
public static class Watchdog
{
    public const string Arg = "--watchdog";
    /// Ярлык «Windows Shell», кнопка в уведомлении: запустить, а если работает — перезапустить.
    public const string LaunchArg = "--launch";
    /// Остановить оболочку и сторожа (скрипт обновления перед сборкой).
    public const string StopArg = "--stop";
    /// Проверка сторожа: `--simulate crash` или `--simulate hang` в работающей оболочке.
    public const string SimulateArg = "--simulate";
    /// Оболочку поднял сторож после сбоя.
    public const string RecoveredArg = "--recovered";
    public const string RestartArg = "--restart";

    const string WindowClass = "WindowsShellNative.Watchdog";
    const string ShellClass = "WindowsShellNative.Messages";
    const string Exe = "WindowsShellNative";

    // Сторожу.
    static readonly uint WM_HELLO = Msg("Hello"), WM_BYE = Msg("Bye"), WM_DUMP = Msg("Dump"), WM_LAUNCH = Msg("Launch"), WM_STOP = Msg("Stop");
    // Оболочке (её окну сообщений).
    public static readonly uint ShellRestart = Msg("Restart"), ShellQuit = Msg("Quit"), ShellSimulate = Msg("Simulate");

    /// Почему оболочка закрывается сама (не сбой).
    public enum Leave { Quit = 0, Restart = 1, Session = 2 }

    static uint Msg(string name) => RegisterWindowMessageW("WindowsShellNative.Watchdog." + name);

    // ---------- со стороны оболочки ----------

    /// Режимы командной строки. true — режим отработал, оболочку не запускаем.
    public static bool Cli(string[] args)
    {
        var i = Array.IndexOf(args, Arg);
        if (i >= 0)
        {
            Run(i + 1 < args.Length && int.TryParse(args[i + 1], out var pid) ? pid : 0);
            return true;
        }
        if (args.Contains(StopArg)) { Stop(); return true; }
        i = Array.IndexOf(args, SimulateArg);
        if (i >= 0)
        {
            var what = i + 1 < args.Length ? args[i + 1] : "";
            PostShell(ShellSimulate, what == "hang" ? 2 : 1);
            return true;
        }
        if (args.Contains(LaunchArg))
        {
            // Ссылка из уведомления: кнопку жмут только у «Запустить ещё раз», клик по самому
            // уведомлению «перезапущена» ничего не делает.
            if (args.Any(a => a.StartsWith(Toasts.Protocol + ":", StringComparison.OrdinalIgnoreCase) && !a.Contains("launch", StringComparison.OrdinalIgnoreCase)))
                return true;
            var h = Find();
            if (h == IntPtr.Zero) return false; // сторожа нет: запускаемся как обычно
            PostMessageW(h, WM_LAUNCH, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        return false;
    }

    static IntPtr Find() => FindWindowW(WindowClass, null);

    /// Окно сообщений оболочки (оно живёт в потоке интерфейса). pid 0 — любой процесс.
    static IntPtr ShellWindow(int pid)
    {
        var h = IntPtr.Zero;
        while ((h = FindWindowExW(IntPtr.Zero, h, ShellClass, null)) != IntPtr.Zero)
            if (pid == 0 || Win32.ProcessId(h) == (uint)pid) return h;
        return IntPtr.Zero;
    }

    /// Сообщение работающей оболочке (её запускали не мы: сторожа нет или это проверка).
    public static bool PostShell(uint msg, int w = 0)
    {
        var h = ShellWindow(0);
        return h != IntPtr.Zero && PostMessageW(h, msg, w, IntPtr.Zero);
    }

    /// Оболочка запустилась и заняла мьютекс: сторож есть — пусть смотрит на нас, нет — запускаем его.
    public static void Attach()
    {
        var h = Find();
        if (h != IntPtr.Zero)
        {
            PostMessageW(h, WM_HELLO, Environment.ProcessId, IntPtr.Zero);
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{Arg} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Exception e)
        {
            Log.Error("запуск сторожа", e);
        }
    }

    /// Оболочка закрывается сама (выход, перезапуск, конец сеанса). false — сторожа нет.
    public static bool Leaving(Leave why)
    {
        var h = Find();
        return h != IntPtr.Zero && PostMessageW(h, WM_BYE, Environment.ProcessId, (int)why);
    }

    /// Необработанное исключение: пока процесс ещё жив, сторож снимает отчёт и снимок памяти.
    public static void DumpMe()
    {
        var h = Find();
        if (h != IntPtr.Zero) SendMessageTimeoutW(h, WM_DUMP, Environment.ProcessId, IntPtr.Zero, SMTO_ABORTIFHUNG, 60_000, out _);
    }

    /// Проверка сторожа (`--simulate`): 1 — падение, 2 — зависание потока интерфейса.
    public static void Simulate(int what)
    {
        Log.Write(what == 2 ? "проверка сторожа: зависаю на 40 с" : "проверка сторожа: падаю");
        if (what == 2) Thread.Sleep(40_000);
        else new Thread(() => throw new InvalidOperationException("проверка сторожа (--simulate crash)")).Start();
    }

    /// `--stop`: оболочка выходит как через «Выход», сторож тоже. Ждём, пока оба процесса
    /// закончатся (иначе сборка не перезапишет exe), упрямые закрываем.
    static void Stop()
    {
        Log.Write("остановка (--stop)");
        var h = Find();
        if (h != IntPtr.Zero) SendMessageTimeoutW(h, WM_STOP, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 5000, out _);
        else PostShell(ShellQuit);
        var sw = Stopwatch.StartNew();
        while (Others().Count > 0 && sw.ElapsedMilliseconds < 20_000) Thread.Sleep(250);
        foreach (var p in Others())
        {
            Log.Write($"остановка: процесс {p.Id} не вышел сам, закрываю");
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
        // Ничего не осталось — панель задач не может остаться спрятанной.
        if (Others().Count == 0) Taskbar.Restore("--stop");
    }

    static List<Process> Others() =>
        Process.GetProcessesByName(Exe).Where(p => p.Id != Environment.ProcessId).ToList();

    // ---------- сам сторож ----------

    static IntPtr _hwnd;
    static WndProc? _proc;
    static Process? _shell;
    static int _pid;
    /// Окно сообщений оболочки: по нему проверяем, отвечает ли поток интерфейса.
    static IntPtr _probe;
    static Leave? _leaving;
    /// Отчёт об этом процессе уже снят (исключение или зависание).
    static bool _reported;
    static string? _hangReason;
    static bool _stopping, _ending, _asleep;
    static long _hungSince = -1, _graceUntil, _lastTick, _lastProbe, _restartAt = -1, _killAt = -1;
    static readonly List<long> Crashes = [];

    const long CheckEveryMs = 5000, HangAfterMs = 10_000, GraceMs = 30_000, RestartDelayMs = 2000;
    const int CrashLimit = 3;
    const long CrashWindowMs = 5 * 60_000;

    static void Run(int pid)
    {
        Log.Prefix = "сторож: ";
        // Не держим папку, из которой нас запустили (WSH-93).
        try { Environment.CurrentDirectory = AppContext.BaseDirectory; } catch { }
        using var mutex = new Mutex(false, "WindowsShellNative.Watchdog");
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            // Второй сторож не нужен: отдаём процесс первому.
            var h = Find();
            if (h != IntPtr.Zero && pid != 0) PostMessageW(h, WM_HELLO, pid, IntPtr.Zero);
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"ОШИБКА (домен): {e.ExceptionObject}");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // При выключении ПК сторож получает сообщения позже оболочки и не принимает её конец за сбой.
        SetProcessShutdownParameters(0x100, 0);
        if (!CreateWindow()) return;
        if (!RegisterHotKey(_hwnd, 1, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 'R'))
            Log.Write("Ctrl+Alt+Shift+R занята другой программой");
        Watch(Get(pid));
        if (_shell is null)
        {
            Log.Write($"процесса {pid} уже нет, выхожу");
            return;
        }
        Log.Write($"смотрю за оболочкой {pid}");
        SetTimer(_hwnd, 1, 500, IntPtr.Zero);
        // Сторож почти всё время спит: отдаём память, набранную при запуске.
        using (var self = Process.GetCurrentProcess()) SetProcessWorkingSetSize(self.Handle, -1, -1);
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Log.Write("выход");
    }

    static bool CreateWindow()
    {
        _proc = Proc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = WindowClass,
        };
        RegisterClassExW(ref wc);
        // Обычное окно верхнего уровня, не HWND_MESSAGE: иначе не дойдут сон и конец сеанса.
        _hwnd = CreateWindowExW(0x80 /* WS_EX_TOOLWINDOW */, WindowClass, "Windows Shell: сторож", 0x80000000 /* WS_POPUP */,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) Log.Write($"окно не создалось ({Marshal.GetLastWin32Error()})");
        return _hwnd != IntPtr.Zero;
    }

    static Process? Get(int pid)
    {
        try { return pid > 0 ? Process.GetProcessById(pid) : null; }
        catch { return null; }
    }

    static void Watch(Process? shell)
    {
        if (!ReferenceEquals(_shell, shell)) _shell?.Dispose();
        _shell = shell;
        _pid = shell?.Id ?? 0;
        _probe = IntPtr.Zero;
        _leaving = null;
        _reported = false;
        _hangReason = null;
        _hungSince = -1;
        _killAt = -1;
        // Запуск оболочки (конфиг, иконки, Пуск) сам по себе занимает поток интерфейса.
        _graceUntil = Environment.TickCount64 + GraceMs;
    }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (msg == WM_HELLO && WM_HELLO != 0)
            {
                var pid = (int)w;
                if (pid != _pid)
                {
                    Log.Write($"теперь смотрю за оболочкой {pid}");
                    Watch(Get(pid));
                    _restartAt = -1;
                }
                return IntPtr.Zero;
            }
            if (msg == WM_BYE && WM_BYE != 0)
            {
                if ((int)w == _pid) _leaving = (Leave)(int)l;
                return IntPtr.Zero;
            }
            if (msg == WM_DUMP && WM_DUMP != 0)
            {
                if ((int)w == _pid && !_reported)
                {
                    _reported = true;
                    CrashReports.Write("необработанное исключение", _pid, stacks: true, dump: true);
                }
                return new IntPtr(1);
            }
            if (msg == WM_LAUNCH && WM_LAUNCH != 0) { Launch("ярлык или уведомление"); return IntPtr.Zero; }
            if (msg == WM_STOP && WM_STOP != 0) { StopShell(); return new IntPtr(1); }
            switch (msg)
            {
                case WM_TIMER: Tick(); return IntPtr.Zero;
                case WM_HOTKEY: Launch("Ctrl+Alt+Shift+R"); return IntPtr.Zero;
                case WM_QUERYENDSESSION:
                    _ending = true;
                    return new IntPtr(1);
                case WM_ENDSESSION:
                    // Выключение отменили — снова сторожим.
                    _ending = w != IntPtr.Zero;
                    return IntPtr.Zero;
                case WM_POWERBROADCAST:
                    if ((int)w == PBT_APMSUSPEND) _asleep = true;
                    else if ((int)w is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
                    {
                        _asleep = false;
                        _hungSince = -1;
                        _graceUntil = Environment.TickCount64 + GraceMs;
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error($"сообщение 0x{msg:X}", e);
        }
        return DefWindowProcW(h, msg, w, l);
    }

    static void Tick()
    {
        var now = Environment.TickCount64;
        // Таймер молчал дольше 10 с — ПК спал (сообщение о сне приходит не всегда).
        if (_lastTick != 0 && now - _lastTick > 10_000)
        {
            _hungSince = -1;
            _graceUntil = now + GraceMs;
        }
        _lastTick = now;
        if (_restartAt >= 0 && now >= _restartAt)
        {
            _restartAt = -1;
            StartShell(RecoveredArg);
            if (_shell != null) Toasts.Show("Оболочка перезапущена после сбоя", "Причина записана в отчёт.", silent: true);
            return;
        }
        if (_shell is null) return;
        if (_shell.HasExited) { OnExit(); return; }
        if (_killAt >= 0 && now >= _killAt)
        {
            _killAt = -1;
            Log.Write("оболочка не закрылась сама за 10 с, закрываю");
            Kill();
            return;
        }
        if (now - _lastProbe < CheckEveryMs || now < _graceUntil || _asleep || _ending) return;
        _lastProbe = now;
        if (Responsive(timeoutMs: 3000))
        {
            if (_hungSince >= 0) Log.Write($"оболочка снова отвечает (не отвечала {(now - _hungSince) / 1000} с)");
            _hungSince = -1;
            return;
        }
        if (_hungSince < 0)
        {
            _hungSince = now;
            Log.Write("оболочка не отвечает");
            return;
        }
        if (now - _hungSince < HangAfterMs) return;
        _hungSince = -1;
        Log.Write("оболочка не отвечает 15 с: снимаю отчёт и перезапускаю");
        _hangReason = "зависла";
        _reported = true;
        CrashReports.Write("зависание", _pid, stacks: true, dump: true);
        Kill();
    }

    /// Поток интерфейса оболочки принимает сообщения. Окна ещё нет — считаем, что отвечает.
    static bool Responsive(int timeoutMs)
    {
        if (_probe == IntPtr.Zero || !IsWindow(_probe)) _probe = ShellWindow(_pid);
        if (_probe == IntPtr.Zero) return true;
        return SendMessageTimeoutW(_probe, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, (uint)timeoutMs, out _) != IntPtr.Zero;
    }

    static void Kill()
    {
        try { _shell?.Kill(); } catch (Exception e) { Log.Error("закрыть оболочку", e); }
    }

    static void OnExit()
    {
        var pid = _pid;
        int code;
        try { code = _shell!.ExitCode; } catch { code = 0; }
        var leaving = _leaving;
        var reported = _reported;
        var hang = _hangReason;
        _shell!.Dispose();
        _shell = null;
        _pid = 0;
        _probe = IntPtr.Zero;
        _killAt = -1;
        if (_stopping || leaving == Leave.Quit)
        {
            // «Выход» оболочка сама вернула панель; убитая по таймауту — возвращаем мы.
            Taskbar.Restore(_stopping ? "остановка" : "выход");
            Log.Write(_stopping ? "оболочка остановлена" : "оболочка закрыта через «Выход»");
            PostQuitMessage(0);
            return;
        }
        if (leaving == Leave.Session || _ending || GetSystemMetrics(SM_SHUTTINGDOWN) != 0)
        {
            Log.Write("Windows завершает работу");
            PostQuitMessage(0);
            return;
        }
        if (leaving == Leave.Restart)
        {
            Log.Write("перезапуск оболочки");
            StartShell(RestartArg);
            return;
        }
        var what = hang ?? $"упала (код 0x{code:X8})";
        Log.Write($"оболочка {what}");
        if (!reported) CrashReports.Write($"сбой, код выхода 0x{code:X8}", pid, stacks: false, dump: false);
        Crash(what);
    }

    /// Ещё один сбой: поднимаем через 2 с или, если падает по кругу, сдаёмся.
    static void Crash(string what)
    {
        var now = Environment.TickCount64;
        Crashes.Add(now);
        Crashes.RemoveAll(t => now - t > CrashWindowMs);
        if (Crashes.Count < CrashLimit)
        {
            _restartAt = now + RestartDelayMs;
            return;
        }
        Log.Write($"{Crashes.Count} сбоя за 5 минут ({what}): больше не поднимаю, панель задач Windows возвращена");
        Crashes.Clear();
        Taskbar.Restore("оболочка не запускается");
        Toasts.Show("Оболочка не запускается", "Она упала 3 раза за 5 минут, панель задач Windows вернулась. Запустить снова: кнопка ниже, ярлык «Windows Shell» или Ctrl+Alt+Shift+R.",
            silent: false, button: "Запустить ещё раз");
    }

    static void StartShell(string arg)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arg)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            })!;
            Log.Write($"оболочка запущена ({(arg.Length > 0 ? arg : "обычный запуск")}), процесс {p.Id}");
            Watch(p);
        }
        catch (Exception e)
        {
            Log.Error("запуск оболочки", e);
            Crash("не запускается");
        }
    }

    /// Ярлык, кнопка в уведомлении или Ctrl+Alt+Shift+R: не работает — запустить, работает —
    /// перезапустить (зависла — сразу, с отчётом).
    static void Launch(string from)
    {
        Crashes.Clear();
        _stopping = false;
        if (_shell is null || _shell.HasExited)
        {
            if (_shell != null) return; // выход ещё не разобран: следующий тик сам решит
            Log.Write($"запуск ({from})");
            _restartAt = -1;
            StartShell("");
            return;
        }
        if (_leaving == Leave.Restart) return; // уже перезапускается
        Log.Write($"перезапуск ({from})");
        _leaving = Leave.Restart;
        if (Responsive(timeoutMs: 2000) && PostMessageW(_probe, ShellRestart, IntPtr.Zero, IntPtr.Zero))
        {
            _killAt = Environment.TickCount64 + 10_000;
            return;
        }
        _reported = true;
        CrashReports.Write($"зависание, перезапуск вручную ({from})", _pid, stacks: true, dump: true);
        Kill();
    }

    static void StopShell()
    {
        _stopping = true;
        _restartAt = -1;
        if (_shell is null || _shell.HasExited)
        {
            if (_shell is null) PostQuitMessage(0);
            return;
        }
        if (Responsive(timeoutMs: 2000) && _probe != IntPtr.Zero && PostMessageW(_probe, ShellQuit, IntPtr.Zero, IntPtr.Zero))
            _killAt = Environment.TickCount64 + 10_000;
        else Kill();
    }

    const uint WM_TIMER = 0x0113, WM_HOTKEY = 0x0312, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_POWERBROADCAST = 0x0218;
    const int PBT_APMSUSPEND = 0x4, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    const uint SMTO_ABORTIFHUNG = 0x2;
    const int SM_SHUTTINGDOWN = 0x2000;

    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr proc);
    static void SetTimer(IntPtr h, int id, uint ms, IntPtr proc) => SetTimer(h, (UIntPtr)id, ms, proc);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("kernel32.dll")] static extern bool SetProcessShutdownParameters(uint level, uint flags);
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
