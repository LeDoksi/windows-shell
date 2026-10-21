using System.Runtime.InteropServices;
using System.Text;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Native;

/// Ярлык «Windows Shell» на рабочем столе и в меню Пуск Windows (WSH-101): запускает оболочку,
/// а если она работает — перезапускает (`--launch`). Без сборки, это не «обновить».
/// Создаём один раз; удалённый с рабочего стола не возвращаем. Сборка переехала — путь поправляем.
public static class Shortcuts
{
    const string Name = "Windows Shell.lnk";
    static string Desktop => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name);
    static string StartMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name);
    static readonly string Marker = Path.Combine(Config.ShellConfig.Dir, "shortcuts.txt");

    public static void Refresh()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var made = File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : null;
            var moved = !string.Equals(made, exe, StringComparison.OrdinalIgnoreCase);
            if (made is null || moved && File.Exists(Desktop)) Save(Desktop, exe);
            // В Пуске ярлык нужен и уведомлениям (по нему Windows знает имя и значок).
            if (moved || !File.Exists(StartMenu)) Save(StartMenu, exe);
            if (moved)
            {
                File.WriteAllText(Marker, exe);
                Log.Write($"ярлыки «Windows Shell»: {exe}");
            }
        }
        catch (Exception e)
        {
            Log.Error("ярлыки «Windows Shell»", e);
        }
    }

    static void Save(string path, string exe)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
        try
        {
            link.SetPath(exe);
            link.SetArguments(Watchdog.LaunchArg);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetIconLocation(exe, 0);
            link.SetDescription("Запустить Windows Shell, а если она работает — перезапустить");
            var store = (IPropertyStore)link;
            var key = PKEY_AppUserModel_ID;
            var pv = new PROPVARIANT { vt = VT_LPWSTR, p = Marshal.StringToCoTaskMemUni(Toasts.Aumid) };
            try
            {
                store.SetValue(ref key, ref pv);
                store.Commit();
            }
            finally { PropVariantClear(ref pv); }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}
