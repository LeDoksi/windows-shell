using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsShell.Native;

/// Значок корзины на рабочем столе (design-v2 п. 3): прячется, пока работает оболочка, и
/// возвращается при выходе, остановке или когда сторож сдался. Как с панелью задач: файл
/// состояния значит «спрятали мы», в нём прежнее значение, чтобы вернуть ровно как было.
public static class DesktopBin
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    const string Bin = "{645FF040-5081-101B-9F08-00AA002F954E}";
    static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsShell", "desktop-bin.txt");

    public static void Hide()
    {
        try
        {
            if (File.Exists(StateFile)) return;
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            var before = k.GetValue(Bin) as int?;
            // Уже спрятан самим человеком — не наше, ничего не трогаем.
            if (before == 1) return;
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, $"before={(before is { } b ? b.ToString() : "none")}\n");
            k.SetValue(Bin, 1, RegistryValueKind.DWord);
            Refresh();
            Log.Write("корзина на рабочем столе спрятана");
        }
        catch (Exception e) { Log.Error("спрятать корзину на рабочем столе", e); }
    }

    public static void Restore(string reason)
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var before = File.ReadAllText(StateFile).Split('=', '\n') is [_, var v, ..] ? v.Trim() : "none";
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (int.TryParse(before, out var n)) k.SetValue(Bin, n, RegistryValueKind.DWord);
                else k.DeleteValue(Bin, throwOnMissingValue: false);
            }
            File.Delete(StateFile);
            Refresh();
            Log.Write($"корзина на рабочем столе возвращена ({reason})");
        }
        catch (Exception e) { Log.Error("вернуть корзину на рабочий стол", e); }
    }

    /// Рабочий стол перечитывает значки: общее «ассоциации поменялись» и F5 самому списку значков.
    static void Refresh()
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        var view = DesktopView();
        if (view != IntPtr.Zero) PostMessageW(view, WM_COMMAND, (IntPtr)0x7103 /* «Обновить» */, IntPtr.Zero);
    }

    /// SHELLDLL_DefView живёт в Progman, а после смены обоев — в одном из WorkerW.
    static IntPtr DesktopView()
    {
        var progman = FindWindowW("Progman", null);
        var view = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var w = IntPtr.Zero;
        while (view == IntPtr.Zero && (w = FindWindowExW(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
            view = FindWindowExW(w, IntPtr.Zero, "SHELLDLL_DefView", null);
        return view;
    }

    const int SHCNE_ASSOCCHANGED = 0x08000000;
    const uint SHCNF_IDLIST = 0, WM_COMMAND = 0x0111;

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
