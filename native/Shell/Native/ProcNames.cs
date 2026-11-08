using System.Collections.Concurrent;

namespace WindowsShell.Native;

/// Имя процесса окна (без .exe) с запоминанием: правила fullscreen спрашивают его 4 раза
/// в секунду, а Process.GetProcessById — дорого. Номер процесса Windows переиспользует
/// не скоро; на всякий случай память сбрасывается, когда в ней больше 256 записей.
public static class ProcNames
{
    static readonly ConcurrentDictionary<uint, string> Cache = new();

    public static string Of(IntPtr hwnd) => Of(Win32.ProcessId(hwnd));

    public static string Of(uint pid)
    {
        if (Cache.TryGetValue(pid, out var name)) return name;
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return "?"; }
        if (Cache.Count > 256) Cache.Clear();
        Cache[pid] = name;
        return name;
    }
}
