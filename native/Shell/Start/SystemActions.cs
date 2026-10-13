using System.Runtime.InteropServices;
using Microsoft.Win32;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Start;

/// Недавние файлы Windows (папка «Недавние», её же показывает Проводник) — раздел 3.5.
public static class RecentFiles
{
    static readonly string Dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    /// Последние открытые файлы (не папки), которые ещё существуют. Фоновый поток.
    public static List<StartEntry> Load(int take)
    {
        var list = new List<StartEntry>();
        try
        {
            var links = new DirectoryInfo(Dir).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(60);
            foreach (var link in links)
            {
                var target = ShellProperty(link.FullName, PKEY_Link_TargetParsingPath);
                if (string.IsNullOrEmpty(target) || !File.Exists(target)) continue;
                if (list.Any(x => string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new StartEntry(ResultKind.File, Path.GetFileName(target), target, When(link.LastWriteTime)) { Args = link.FullName });
                if (list.Count >= take) break;
            }
        }
        catch (Exception e) { Log.Error("недавние файлы", e); }
        return list;
    }

    static string When(DateTime t)
    {
        var now = DateTime.Now;
        if (t.Date == now.Date) return $"Сегодня, {t:HH:mm}";
        if (t.Date == now.Date.AddDays(-1)) return $"Вчера, {t:HH:mm}";
        return t.ToString("d MMMM");
    }

    /// «Убрать из списка»: удаляем ярлык из «Недавних», как делает Проводник.
    public static void Remove(StartEntry entry)
    {
        try { if (entry.Args is { } lnk && lnk.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) File.Delete(lnk); }
        catch (Exception e) { Log.Error("убрать из недавних", e); }
    }
}

/// Пользователь: имя и аватар Windows (раздел 3.6).
public static class UserInfo
{
    public static string Name
    {
        get
        {
            try
            {
                var size = 256u;
                var sb = new System.Text.StringBuilder((int)size);
                // NameDisplay — «Георгий Шаков»; у локальной учётной записи бывает пусто.
                if (GetUserNameExW(3, sb, ref size) && sb.Length > 0) return sb.ToString();
            }
            catch { }
            return Environment.UserName;
        }
    }

    /// Путь к картинке учётной записи или null.
    public static string? Picture
    {
        get
        {
            try
            {
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                if (sid is null) return null;
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
                foreach (var name in new[] { "Image192", "Image96", "Image208", "Image240", "Image448" })
                    if (key?.GetValue(name) is string p && File.Exists(p)) return p;
            }
            catch { }
            return null;
        }
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserNameExW(int format, System.Text.StringBuilder name, ref uint size);
}

/// Питание и выход (раздел 3.6): выполняются сразу, без вопросов, как в Windows.
public static class Power
{
    /// Windows ждёт перезагрузки для установки обновлений: вместо «Завершение работы» и
    /// «Перезагрузка» — «Обновить и …».
    public static bool UpdatesPending
    {
        get
        {
            try
            {
                using var a = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
                if (a != null) return true;
                using var b = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
                return b != null;
            }
            catch { return false; }
        }
    }

    public static void Lock() => LockWorkStation();

    public static void Sleep()
    {
        EnableShutdownPrivilege();
        // Если включена гибернация, Windows всё равно уйдёт в сон: hibernate = false.
        if (!SetSuspendState(false, false, false)) Log.Write($"сон: ошибка {Marshal.GetLastWin32Error()}");
    }

    public static void SignOut()
    {
        if (!ExitWindowsEx(EWX_LOGOFF, SHTDN_REASON_FLAG_PLANNED)) Log.Write($"выход: ошибка {Marshal.GetLastWin32Error()}");
    }

    /// «Сменить пользователя»: экран входа, а эта сессия остаётся открытой.
    public static void SwitchUser()
    {
        if (!WTSDisconnectSession(IntPtr.Zero, -1, false)) Lock();
    }

    public static void Shutdown(bool updates) => Initiate(SHUTDOWN_POWEROFF, updates, hybrid: FastStartup);

    public static void Restart(bool updates) => Initiate(SHUTDOWN_RESTART, updates, hybrid: false);

    /// «Быстрый запуск» включён: Windows выключается так же (гибридно), как из своего Пуска.
    static bool FastStartup
    {
        get
        {
            try { return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0) is int v && v == 1; }
            catch { return false; }
        }
    }

    static void Initiate(uint flags, bool updates, bool hybrid)
    {
        EnableShutdownPrivilege();
        if (updates) flags |= SHUTDOWN_INSTALL_UPDATES;
        var r = InitiateShutdownW(null, null, 0, flags | (hybrid ? SHUTDOWN_HYBRID : 0), SHTDN_REASON_FLAG_PLANNED);
        if (r != 0 && hybrid) r = InitiateShutdownW(null, null, 0, flags, SHTDN_REASON_FLAG_PLANNED);
        if (r != 0) Log.Write($"выключение: ошибка {r}");
    }

    static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid)) return;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    /// Это ноутбук (есть батарея): в меню Win+X появляется «Мобильность».
    public static bool HasBattery => GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255;

    const uint EWX_LOGOFF = 0, SHTDN_REASON_FLAG_PLANNED = 0x80000000;
    const uint SHUTDOWN_RESTART = 0x4, SHUTDOWN_POWEROFF = 0x8, SHUTDOWN_INSTALL_UPDATES = 0x40, SHUTDOWN_HYBRID = 0x200;
    const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 2;

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll", SetLastError = true)] static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("powrprof.dll", SetLastError = true)] static extern bool SetSuspendState(bool hibernate, bool force, bool wakeupEventsDisabled);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint InitiateShutdownW(string? machine, string? message, uint grace, uint flags, uint reason);
    [DllImport("wtsapi32.dll")] static extern bool WTSDisconnectSession(IntPtr server, int session, bool wait);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll")] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
