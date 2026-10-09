using System.Runtime.InteropServices;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Apps;

/// Где программа закрепления и есть ли она (у ярлыков — цель ярлыка).
public sealed record AppTarget(bool Exists, string? Exe);

/// Запуск через оболочку Windows, как двойной клик в Проводнике (как `launch.rs`).
public static class Launcher
{
    public static bool IsPath(string target) =>
        target.StartsWith(@"\\") || target.StartsWith('/')
        || (target.Length >= 3 && char.IsAsciiLetter(target[0]) && target[1] == ':' && (target[2] == '\\' || target[2] == '/'));

    /// Что отдать ShellExecuteEx и Проводнику: путь или `shell:AppsFolder\<AUMID>`.
    public static string ShellName(string target) => IsPath(target) ? target : $"shell:AppsFolder\\{target}";

    static readonly Dictionary<string, AppTarget> Targets = new(StringComparer.OrdinalIgnoreCase);

    public static AppTarget Resolve(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return new(false, null);
        if (Targets.TryGetValue(target, out var cached)) return cached;
        AppTarget result;
        if (IsPath(target))
        {
            var exists = File.Exists(target) || Directory.Exists(target);
            var exe = target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ShellProperty(target, PKEY_Link_TargetParsingPath)
                : target;
            result = new(exists, exe);
        }
        else
        {
            var name = ShellProperty($"shell:AppsFolder\\{target}", PKEY_ItemNameDisplay);
            var exe = ShellProperty($"shell:AppsFolder\\{target}", PKEY_Link_TargetParsingPath);
            if (exe != null && !IsPath(exe)) exe = null;
            exe ??= KnownFolderPath(target);
            result = new(name != null, exe);
        }
        Targets[target] = result;
        return result;
    }

    /// `{GUID}\путь\app.exe` — так «Пуск» называет программы без своего AUMID.
    static string? KnownFolderPath(string id)
    {
        if (!id.StartsWith('{')) return null;
        var end = id.IndexOf("}\\", StringComparison.Ordinal);
        if (end < 0 || !Guid.TryParse(id[1..end], out var guid)) return null;
        try
        {
            if (SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var p) != 0) return null;
            var path = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            return path is null ? null : Path.Combine(path, id[(end + 2)..]);
        }
        catch { return null; }
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    /// Запуск. `admin` — от имени администратора (Windows спросит разрешение).
    public static bool Launch(string target, bool admin = false)
    {
        var file = ShellName(target);
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOASYNC,
            lpVerb = admin ? "runas" : null,
            lpFile = file,
            lpDirectory = IsPath(target) ? Path.GetDirectoryName(target) : null,
            nShow = 1,
        };
        return ShellExecuteExW(ref info);
    }

    /// Запуск в своём потоке: ShellExecuteEx бывает медленным, а интерфейс не должен замирать.
    public static void LaunchInBackground(string target, bool admin = false)
    {
        var t = new Thread(() => Launch(target, admin)) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// Открыть адрес как есть (`ms-settings:sound`, `taskmgr.exe`) в своём потоке.
    public static void Open(string file)
    {
        var t = new Thread(() =>
        {
            var info = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), fMask = SEE_MASK_NOASYNC, lpFile = file, nShow = 1 };
            ShellExecuteExW(ref info);
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// «Открыть расположение файла»: Проводник с выделенным файлом.
    public static void Reveal(string path)
    {
        var pidl = ILCreateFromPathW(path);
        if (pidl == IntPtr.Zero) return;
        try { SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0); }
        finally { ILFree(pidl); }
    }
}
