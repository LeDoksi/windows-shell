using System.Runtime.InteropServices;
using System.Text;
using WindowsShell.Config;

namespace WindowsShell.Native;

/// Следит за переименованием и перемещением папок и файлов (как Проводник узнаёт о них) и
/// переписывает пути в доке, категориях и в наших копиях ярлыков: переименовал папку с
/// программой — плитка продолжает запускать её (WSH-98).
public sealed class PathWatch
{
    const uint WM_PATHCHANGE = 0x8000 + 2; // WM_APP + 2
    const int SHCNE_RENAMEITEM = 0x1, SHCNE_RENAMEFOLDER = 0x20000;
    const int SHCNRF_ShellLevel = 0x2, SHCNRF_InterruptLevel = 0x1, SHCNRF_NewDelivery = 0x8000;
    const int CSIDL_DESKTOP = 0;

    readonly ShellConfig _config;

    public PathWatch(MessageWindow window, ShellConfig config)
    {
        _config = config;
        window.Message += OnMessage;
        if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DESKTOP, out var desktop) != 0) { Log.Write("пути: нет папки рабочего стола"); return; }
        var entry = new SHChangeNotifyEntry { pidl = desktop, fRecursive = true };
        var id = SHChangeNotifyRegister(window.Hwnd, SHCNRF_ShellLevel | SHCNRF_InterruptLevel | SHCNRF_NewDelivery,
            SHCNE_RENAMEITEM | SHCNE_RENAMEFOLDER, WM_PATHCHANGE, 1, ref entry);
        if (id == 0) Log.Write("пути: подписка на переименования не удалась");
    }

    void OnMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (msg != WM_PATHCHANGE) return;
        var lk = SHChangeNotification_Lock(w, (uint)(long)l, out var pidls, out var ev);
        if (lk == IntPtr.Zero) return;
        string? from, to;
        try
        {
            if ((ev & (SHCNE_RENAMEITEM | SHCNE_RENAMEFOLDER)) == 0) return;
            from = PathOf(Marshal.ReadIntPtr(pidls));
            to = PathOf(Marshal.ReadIntPtr(pidls, IntPtr.Size));
        }
        finally { SHChangeNotification_Unlock(lk); }
        if (from is null || to is null || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        Renamed(from, to);
    }

    static string? PathOf(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero) return null;
        var sb = new StringBuilder(1024);
        return SHGetPathFromIDListW(pidl, sb) && sb.Length > 0 ? sb.ToString().TrimEnd('\\') : null;
    }

    /// `path` лежит в `from` (или это он сам) — тот же путь внутри `to`.
    public static string? Moved(string path, string from, string to)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (string.Equals(path, from, StringComparison.OrdinalIgnoreCase)) return to;
        if (path.Length > from.Length && path.StartsWith(from, StringComparison.OrdinalIgnoreCase) && (path[from.Length] is '\\' or '/'))
            return to + path[from.Length..];
        return null;
    }

    void Renamed(string from, string to)
    {
        var d = _config.Data;
        var hits = d.Dock.Pinned.Any(p => Moved(p.Target, from, to) != null)
            || d.Categories.Any(c => Moved(c.SourceFolder ?? "", from, to) != null || c.Items.Any(i => Moved(i.Target, from, to) != null));
        if (hits)
        {
            _config.Update(c =>
            {
                c.Dock.Pinned = c.Dock.Pinned.Select(p => Moved(p.Target, from, to) is { } t ? p with { Target = t } : p).ToList();
                foreach (var cat in c.Categories)
                {
                    if (Moved(cat.SourceFolder ?? "", from, to) is { } sf) cat.SourceFolder = sf;
                    foreach (var it in cat.Items)
                    {
                        if (Moved(it.Target, from, to) is not { } t) continue;
                        // Плитка называлась по имени файла — переименовали файл, меняем и подпись.
                        if (string.Equals(it.Target, from, StringComparison.OrdinalIgnoreCase) && it.Kind is "file" or "folder"
                            && string.Equals(it.Title, Path.GetFileName(from), StringComparison.OrdinalIgnoreCase))
                            it.Title = Path.GetFileName(to);
                        it.Target = t;
                    }
                }
            });
            Log.Write($"пути: «{from}» → «{to}», плитки обновлены");
        }
        FixShortcuts(from, to);
    }

    /// Наши копии ярлыков с рабочего стола: цель и рабочая папка — на новое место.
    static void FixShortcuts(string from, string to)
    {
        var dir = Path.Combine(ShellConfig.Dir, "shortcuts");
        if (!Directory.Exists(dir)) return;
        // Переименовали саму нашу папку — нечего чинить.
        if (Moved(dir, from, to) != null) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.lnk"))
        {
            object? obj = null;
            try
            {
                obj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
                var persist = (IPersistFile)obj!;
                if (persist.Load(file, 2 /* STGM_READWRITE */) != 0) continue;
                var link = (IShellLinkW)obj!;
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
                var target = Moved(Environment.ExpandEnvironmentVariables(sb.ToString()), from, to);
                if (target is null) continue;
                link.SetPath(target);
                sb.Clear();
                link.GetWorkingDirectory(sb, sb.Capacity);
                if (Moved(sb.ToString(), from, to) is { } wd) link.SetWorkingDirectory(wd);
                persist.Save(null, true);
                Log.Write($"пути: ярлык {Path.GetFileName(file)} → {target}");
            }
            catch (Exception e) { Log.Error($"ярлык {file}", e); }
            finally { if (obj != null) Marshal.ReleaseComObject(obj); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHChangeNotifyEntry { public IntPtr pidl; [MarshalAs(UnmanagedType.Bool)] public bool fRecursive; }

    [DllImport("shell32.dll")] static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);
    [DllImport("shell32.dll")] static extern uint SHChangeNotifyRegister(IntPtr hwnd, int sources, int events, uint msg, int count, ref SHChangeNotifyEntry entry);
    [DllImport("shell32.dll")] static extern IntPtr SHChangeNotification_Lock(IntPtr change, uint pid, out IntPtr pidls, out int ev);
    [DllImport("shell32.dll")] static extern bool SHChangeNotification_Unlock(IntPtr lk);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? file, [MarshalAs(UnmanagedType.Bool)] bool remember);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr pidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        [PreserveSig] int GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        [PreserveSig] int GetHotkey(out short key);
        [PreserveSig] int SetHotkey(short key);
        [PreserveSig] int GetShowCmd(out int cmd);
        [PreserveSig] int SetShowCmd(int cmd);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
