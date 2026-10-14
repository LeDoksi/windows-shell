using System.Diagnostics;
using System.Runtime.InteropServices;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Native;

/// Панель задач Windows (WSH-41, WSH-42): прячем на всех мониторах и гарантированно возвращаем.
/// Как спайк WSH-43 (`spike_taskbar.rs`): сначала сохраняем исходное автоскрытие в файл, потом
/// поднимаем сторожа, потом прячем. Возврат, от частого к аварийному:
/// 1. выход и перезапуск оболочки, Ctrl+Alt+Shift+T, пункт меню трея;
/// 2. процесс-сторож (тот же exe с `--taskbar-watchdog <pid>`): ждёт смерти оболочки и возвращает;
/// 3. следующий запуск: файл состояния остался — значит, прошлый раз не вернули;
/// 4. `scripts/restore-taskbar.cmd` — вообще без нашего exe (тот же файл состояния).
public static class Taskbar
{
    public const string WatchdogArg = "--taskbar-watchdog";
    public const string RestoreArg = "--restore-taskbar";

    /// Файл с исходным автоскрытием. Есть файл — панель сейчас «наша» и её надо вернуть.
    /// Путь тот же, что у спайка и `restore-taskbar.ps1`.
    static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsShell", "taskbar-state.txt");

    static volatile bool _hiding;
    static Thread? _poll;
    static Process? _watchdog;

    /// Сейчас прячем.
    public static bool Hidden => _hiding;

    /// Режимы командной строки. true — режим отработал, оболочку не запускаем.
    public static bool Cli(string[] args)
    {
        if (args.Contains(RestoreArg))
        {
            Restore("--restore-taskbar");
            return true;
        }
        var i = Array.IndexOf(args, WatchdogArg);
        if (i < 0) return false;
        var pid = i + 1 < args.Length && int.TryParse(args[i + 1], out var p) ? p : 0;
        Log.Write($"сторож панели задач ждёт процесс {pid}");
        try
        {
            using var shell = Process.GetProcessById(pid);
            shell.WaitForExit();
        }
        catch { }
        // Оболочка ушла сама и всё вернула — файла нет. Перезапуск: файл уже у нового процесса.
        if (Owner() == pid) Restore("сторож: оболочка завершилась");
        else Log.Write("сторож: оболочка завершилась, панель уже возвращена");
        return true;
    }

    /// Спрятать панель задач на всех мониторах. `tray` — работает ли наш трей (тогда при
    /// возврате программы перерегистрируют иконки у Проводника).
    public static void Hide(bool tray)
    {
        if (_hiding) return;
        // Файл остался от прошлого раза (перезапуск оболочки или оба процесса убиты): панель
        // уже спрятана нами, исходное автоскрытие берём из файла, а не у спрятанной панели.
        bool autohide;
        try { autohide = File.Exists(StateFile) ? File.ReadAllText(StateFile).Contains("autohide=1") : (AppBar(ABM_GETSTATE, 0) & ABS_AUTOHIDE) != 0; }
        catch { autohide = (AppBar(ABM_GETSTATE, 0) & ABS_AUTOHIDE) != 0; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, $"autohide={(autohide ? 1 : 0)}\ntray={(tray ? 1 : 0)}\npid={Environment.ProcessId}\n");
        }
        catch (Exception e)
        {
            // Не можем запомнить, как было, — не прячем.
            Log.Error("файл состояния панели задач", e);
            return;
        }
        StartWatchdog();
        // Автоскрытие отдаёт место панели под окна программ; само окно прячем поверх.
        AppBar(ABM_SETSTATE, ABS_AUTOHIDE);
        _hiding = true;
        Log.Write($"панель задач спрятана ({SetVisible(false)} окон), автоскрытие было {(autohide ? "вкл" : "выкл")}");
        // Проводник иногда показывает панель сам (смена мониторов, перезапуск): проверяем дважды
        // в секунду. Это три FindWindowEx — заметной нагрузки нет.
        if (_poll is null)
        {
            _poll = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(500);
                    if (_hiding) HideAgain("проверка");
                }
            }) { IsBackground = true, Name = "taskbar" };
            _poll.Start();
        }
    }

    /// Вернуть панель задач. Безопасно звать сколько угодно раз и из любого процесса.
    public static void Restore(string reason)
    {
        _hiding = false;
        try
        {
            if (File.Exists(StateFile))
            {
                var saved = File.ReadAllText(StateFile);
                var autohide = saved.Contains("autohide=1");
                AppBar(ABM_SETSTATE, autohide ? ABS_AUTOHIDE : 0);
                File.Delete(StateFile);
                if (saved.Contains("tray=1"))
                {
                    // Наш трей перехватывал иконки. Его окна уже нет (или вот-вот не будет) —
                    // просим программы заново отдать иконки Проводнику.
                    SendNotifyMessageW(HWND_BROADCAST, RegisterWindowMessageW("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
                }
                Log.Write($"панель задач возвращена ({reason}), автоскрытие {(autohide ? "вкл" : "выкл")}");
            }
            SetVisible(true);
        }
        catch (Exception e)
        {
            Log.Error("возврат панели задач", e);
        }
    }

    /// Процесс оболочки, который сейчас прячет панель (из файла состояния), 0 — никто.
    static int Owner()
    {
        try
        {
            if (!File.Exists(StateFile)) return 0;
            var line = File.ReadAllLines(StateFile).FirstOrDefault(l => l.StartsWith("pid="));
            return line != null && int.TryParse(line[4..], out var pid) ? pid : 0;
        }
        catch { return 0; }
    }

    /// Проводник перезапустился (TaskbarCreated): новая панель снова видна и без автоскрытия.
    public static void OnTaskbarCreated()
    {
        if (!_hiding) return;
        AppBar(ABM_SETSTATE, ABS_AUTOHIDE);
        HideAgain("Проводник перезапущен");
    }

    static void HideAgain(string reason)
    {
        if (!TrayWindows().Any(IsWindowVisible)) return;
        SetVisible(false);
        Log.Write($"панель задач спрятана снова ({reason})");
    }

    static void StartWatchdog()
    {
        if (_watchdog is { HasExited: false }) return;
        try
        {
            _watchdog = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{WatchdogArg} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            // Без сторожа панель всё равно вернётся при следующем запуске или скриптом.
            Log.Error("сторож панели задач", e);
        }
    }

    /// Окна панели задач на всех мониторах (наше окно трея тоже класса Shell_TrayWnd — его не берём).
    static List<IntPtr> TrayWindows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<IntPtr>();
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var h = IntPtr.Zero;
            while ((h = FindWindowExW(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
                if (ProcessId(h) != own) list.Add(h);
        }
        return list;
    }

    /// Окно панели задач Проводника на основном мониторе (не наше).
    public static IntPtr ExplorerTray() => TrayWindows().FirstOrDefault(h => ClassName(h) == "Shell_TrayWnd");

    static int SetVisible(bool show)
    {
        var wins = TrayWindows();
        foreach (var h in wins) ShowWindow(h, show ? SW_SHOW : SW_HIDE);
        return wins.Count;
    }

    static int AppBar(uint msg, int lParam)
    {
        var d = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = ExplorerTray(), lParam = lParam };
        return (int)SHAppBarMessage(msg, ref d);
    }

    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1;
    const int SW_HIDE = 0, SW_SHOW = 5;
    static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage, uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern bool SendNotifyMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
