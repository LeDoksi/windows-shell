using System.Runtime.InteropServices;
using System.Text;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Apps;

/// Пункт списка быстрых действий: действие программы («Новое окно в режиме инкогнито») или
/// недавний файл.
public sealed record JumpEntry(string Title, string Path, string? Args);

/// Списки быстрых действий программ (раздел 8, WSH-85), как в правом клике по кнопке панели
/// задач Windows. Действия программа записывает в CustomDestinations (там сохранённые ярлыки
/// подряд), недавние файлы Windows отдаёт по AUMID программы.
public static class JumpLists
{
    static readonly string CustomDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\CustomDestinations");
    static readonly Dictionary<string, (DateTime Stamp, List<JumpEntry> Links)> Cache = new(StringComparer.OrdinalIgnoreCase);

    // CLSID_ShellLink в том виде, как он лежит в файле перед каждым ярлыком.
    static readonly byte[] ShellLinkClsid = new Guid("00021401-0000-0000-C000-000000000046").ToByteArray();

    /// Действия программы: ярлыки из её файла CustomDestinations, которые запускают её же exe.
    public static List<JumpEntry> Tasks(string? exe)
    {
        if (string.IsNullOrEmpty(exe) || !Directory.Exists(CustomDir)) return [];
        var result = new List<JumpEntry>();
        try
        {
            foreach (var file in Directory.GetFiles(CustomDir, "*.customDestinations-ms"))
            {
                var links = Read(file);
                if (!links.Any(l => string.Equals(l.Path, exe, StringComparison.OrdinalIgnoreCase))) continue;
                result.AddRange(links.Where(l => l.Title.Length > 0));
            }
        }
        catch (Exception e) { Log.Write($"быстрые действия: {e.Message}"); }
        return result.DistinctBy(l => (l.Title, l.Args)).Take(10).ToList();
    }

    static List<JumpEntry> Read(string file)
    {
        var stamp = File.GetLastWriteTimeUtc(file);
        if (Cache.TryGetValue(file, out var c) && c.Stamp == stamp) return c.Links;
        var list = new List<JumpEntry>();
        var bytes = File.ReadAllBytes(file);
        for (var i = IndexOf(bytes, ShellLinkClsid, 0); i >= 0; i = IndexOf(bytes, ShellLinkClsid, i + 16))
        {
            if (LoadLink(bytes, i + 16) is { } link) list.Add(link);
        }
        Cache[file] = (stamp, list);
        return list;
    }

    static JumpEntry? LoadLink(byte[] bytes, int offset)
    {
        object? obj = null;
        var stream = IntPtr.Zero;
        try
        {
            obj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            var data = bytes.AsSpan(offset).ToArray();
            stream = SHCreateMemStream(data, (uint)data.Length);
            if (stream == IntPtr.Zero || ((IPersistStream)obj!).Load(stream) != 0) return null;
            var link = (IShellLinkW)obj;
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            var path = sb.ToString();
            sb.Clear();
            link.GetArguments(sb, sb.Capacity);
            var args = sb.ToString();
            var title = Title((IPropertyStore)obj);
            if (string.IsNullOrEmpty(title))
            {
                sb.Clear();
                link.GetDescription(sb, sb.Capacity);
                title = sb.ToString();
            }
            if (string.IsNullOrEmpty(path)) return null;
            return new JumpEntry(Indirect(title ?? ""), path, args.Length > 0 ? args : null);
        }
        catch { return null; }
        finally
        {
            if (stream != IntPtr.Zero) Marshal.Release(stream);
            if (obj != null) Marshal.ReleaseComObject(obj);
        }
    }

    /// Недавние файлы программы (по её AUMID).
    public static List<JumpEntry> Recent(string? aumid, int count = 8)
    {
        if (string.IsNullOrEmpty(aumid)) return [];
        var result = new List<JumpEntry>();
        object? lists = null;
        try
        {
            lists = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("86BEC222-30F2-47E0-9F25-60D11CD75C28"))!);
            var docs = (IApplicationDocumentLists)lists!;
            if (docs.SetAppID(aumid) != 0) return [];
            var iid = typeof(IObjectArray).GUID;
            if (docs.GetList(0 /* ADLT_RECENT */, (uint)count, ref iid, out var arrayPtr) != 0 || arrayPtr == IntPtr.Zero) return [];
            var array = (IObjectArray)Marshal.GetObjectForIUnknown(arrayPtr);
            Marshal.Release(arrayPtr);
            array.GetCount(out var n);
            var itemIid = typeof(IShellItem).GUID;
            for (uint i = 0; i < n; i++)
            {
                if (array.GetAt(i, ref itemIid, out var itemPtr) != 0 || itemPtr == IntPtr.Zero) continue;
                var item = (IShellItem)Marshal.GetObjectForIUnknown(itemPtr);
                Marshal.Release(itemPtr);
                if (item.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out var p) == 0)
                {
                    var path = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(path)) result.Add(new JumpEntry(System.IO.Path.GetFileName(path), path, null));
                }
                Marshal.ReleaseComObject(item);
            }
            Marshal.ReleaseComObject(array);
        }
        catch (Exception e) { Log.Write($"недавние программы: {e.Message}"); }
        finally { if (lists != null) Marshal.ReleaseComObject(lists); }
        return result;
    }

    static string? Title(IPropertyStore store)
    {
        var key = new PROPERTYKEY("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 2); // System.Title
        if (store.GetValue(ref key, out var v) != 0) return null;
        try { return v.vt == VT_LPWSTR ? Marshal.PtrToStringUni(v.p) : null; }
        finally { PropVariantClear(ref v); }
    }

    /// «@C:\…\app.dll,-123» и «ms-resource:…» — строки из ресурсов программы.
    static string Indirect(string s)
    {
        if (!s.StartsWith('@')) return s;
        var sb = new StringBuilder(512);
        return SHLoadIndirectString(s, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : "";
    }

    static int IndexOf(byte[] data, byte[] pattern, int from)
    {
        var at = data.AsSpan(from).IndexOf(pattern);
        return at < 0 ? -1 : from + at;
    }

    [DllImport("shlwapi.dll", EntryPoint = "#12")] static extern IntPtr SHCreateMemStream(byte[] data, uint size);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHLoadIndirectString(string source, StringBuilder output, uint size, IntPtr reserved);

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistStream
    {
        [PreserveSig] int GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load(IntPtr stream);
        [PreserveSig] int Save(IntPtr stream, bool clearDirty);
        [PreserveSig] int GetSizeMax(out long size);
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
    }

    [ComImport, Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationDocumentLists
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(int listType, uint itemsDesired, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
    }
}
