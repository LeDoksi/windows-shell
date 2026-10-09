using System.Diagnostics;
using Microsoft.UI.Dispatching;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Apps;

/// Одно окно, которое панель задач Windows показала бы.
public sealed record WindowInfo(
    IntPtr Hwnd,
    string Title,
    string? Aumid,
    string? Exe,
    string AppName,
    bool Minimized,
    long Seq,
    long Focused);

/// Следит за окнами верхнего уровня, как трекер версии на Tauri (`apps.rs`): события Windows
/// ставят пересчёт через 60 мс, страховочный пересчёт раз в 2 с. Всё в потоке интерфейса.
public sealed class WindowTracker
{
    static readonly string[] SystemClasses =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"];

    readonly DispatcherQueue _queue;
    readonly DispatcherQueueTimer _debounce;
    readonly DispatcherQueueTimer _safety;
    readonly WinEventProc _proc;
    readonly List<IntPtr> _hooks = [];
    readonly uint _ownPid = (uint)Environment.ProcessId;
    readonly Dictionary<IntPtr, long> _seq = [];
    readonly Dictionary<IntPtr, long> _focused = [];
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    long _tick;

    public IReadOnlyList<WindowInfo> Windows { get; private set; } = [];

    /// Последнее активное чужое окно (корневой владелец). Клик по доку его не меняет.
    public IntPtr Foreground { get; private set; }

    public event Action? Changed;

    /// Сменилось активное окно (чужое).
    public event Action? ForegroundChanged;

    public WindowTracker(DispatcherQueue queue)
    {
        _queue = queue;
        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(60);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Scan();
        _safety = queue.CreateTimer();
        _safety.Interval = TimeSpan.FromSeconds(2);
        _safety.Tick += (_, _) => Scan();
        _proc = OnEvent;
    }

    public void Start()
    {
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
        NoteForeground(GetForegroundWindow());
        Scan();
        _safety.Start();
    }

    public void Stop()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        _safety.Stop();
    }

    public void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    void Hook(uint min, uint max)
    {
        var h = SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0) return;
        if (ev == EVENT_SYSTEM_FOREGROUND) NoteForeground(hwnd);
        // Создание и уничтожение дочерних окон приходит тысячами: интересны только верхние.
        if ((ev == EVENT_OBJECT_CREATE || ev == EVENT_OBJECT_DESTROY || ev == EVENT_OBJECT_NAMECHANGE)
            && GetAncestor(hwnd, 1 /* GA_PARENT */) != IntPtr.Zero
            && GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd)
            return;
        Schedule();
    }

    void NoteForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || ProcessId(hwnd) == _ownPid) return;
        var root = GetAncestor(hwnd, GA_ROOTOWNER);
        if (root == IntPtr.Zero) root = hwnd;
        Foreground = root;
        _focused[root] = ++_tick;
        ForegroundChanged?.Invoke();
    }

    static bool IsAppWindow(IntPtr h, string cls, string title, uint ownPid)
    {
        if (!IsWindowVisible(h) || IsCloaked(h) || string.IsNullOrWhiteSpace(title)) return false;
        if (ProcessId(h) == ownPid) return false;
        if (SystemClasses.Contains(cls)) return false;
        var ex = ExStyle(h);
        if ((ex & WS_EX_APPWINDOW) != 0) return true;
        var tool = (ex & WS_EX_TOOLWINDOW) != 0;
        var noActivate = (ex & WS_EX_NOACTIVATE) != 0;
        var owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
        return !(tool || owned || noActivate);
    }

    /// У UWP окно верхнего уровня принадлежит ApplicationFrameHost; само приложение —
    /// в дочернем CoreWindow другого процесса.
    static uint UwpPid(IntPtr frame, uint framePid)
    {
        uint found = 0;
        EnumChildWindows(frame, (c, _) =>
        {
            var pid = ProcessId(c);
            if (pid != framePid && ClassName(c) == "Windows.UI.Core.CoreWindow") { found = pid; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    string NameOf(string? aumid, string? exe, string title)
    {
        var key = aumid ?? exe ?? title;
        if (_names.TryGetValue(key, out var cached)) return cached;
        string? start = aumid != null ? ShellProperty($"shell:AppsFolder\\{aumid}", PKEY_ItemNameDisplay) : null;
        string? description = null;
        if (exe != null)
        {
            try { description = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
        }
        var stem = exe != null ? Path.GetFileNameWithoutExtension(exe) : null;
        var name = new[] { start, description, stem }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? title;
        _names[key] = name;
        return name;
    }

    public void Scan()
    {
        var handles = new List<IntPtr>();
        EnumWindows((h, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        var processes = new Dictionary<uint, (string? Exe, string? Aumid)>();
        var list = new List<WindowInfo>();
        foreach (var h in handles)
        {
            var title = WindowText(h);
            var cls = ClassName(h);
            if (!IsAppWindow(h, cls, title, _ownPid)) continue;
            var framePid = ProcessId(h);
            var pid = framePid;
            if (cls == "ApplicationFrameWindow")
            {
                var core = UwpPid(h, framePid);
                if (core != 0) pid = core;
            }
            if (!processes.TryGetValue(pid, out var proc)) processes[pid] = proc = ProcessInfo(pid);
            var aumid = WindowAumid(h) ?? proc.Aumid;
            if (!_seq.TryGetValue(h, out var seq)) _seq[h] = seq = ++_tick;
            _focused.TryGetValue(h, out var focused);
            list.Add(new WindowInfo(h, title, aumid, proc.Exe, NameOf(aumid, proc.Exe, title), IsIconic(h), seq, focused));
        }
        // Закрытые окна забываем.
        var alive = list.Select(w => w.Hwnd).ToHashSet();
        foreach (var dead in _seq.Keys.Where(k => !alive.Contains(k)).ToList()) { _seq.Remove(dead); _focused.Remove(dead); }
        list.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        if (!list.SequenceEqual(Windows))
        {
            Windows = list;
            Changed?.Invoke();
        }
    }

    // ---------- действия с окнами ----------

    public void Activate(IntPtr h)
    {
        if (!IsWindow(h)) return;
        if (IsIconic(h)) ShowWindowAsync(h, SW_RESTORE);
        if (!SetForegroundWindow(h))
        {
            // Запасной путь: на время подцепиться к потоку активного окна.
            var fg = GetForegroundWindow();
            var theirs = GetWindowThreadProcessId(fg, out _);
            var ours = GetCurrentThreadId();
            AttachThreadInput(ours, theirs, true);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            AttachThreadInput(ours, theirs, false);
        }
        Schedule();
    }

    public void Minimize(IntPtr h)
    {
        if (IsWindow(h)) ShowWindowAsync(h, SW_MINIMIZE);
        Schedule();
    }

    public void Close(IntPtr h)
    {
        // Как «Закрыть окно» в панели задач.
        if (IsWindow(h)) PostMessageW(h, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);
    }

    /// Вернуть фокус окну, которое было активно до нашего меню или окошка (правило 0.6).
    public void RestoreForeground()
    {
        var h = Foreground;
        if (h != IntPtr.Zero && IsWindow(h) && !IsIconic(h)) SetForegroundWindow(h);
    }

    public bool IsActive(IntPtr h) => h == Foreground && GetForegroundWindow() is var fg
        && (fg == h || GetAncestor(fg, GA_ROOTOWNER) == h || ProcessId(fg) == _ownPid);
}
