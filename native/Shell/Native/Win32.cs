using System.Runtime.InteropServices;
using System.Text;

namespace WindowsShell.Native;

/// Вызовы Windows, которые нужны прототипу. Только то, чем пользуемся.
public static partial class Win32
{
    // ---------- user32: окна ----------

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO info);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] public static extern short GetKeyState(int key);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(IntPtr p, uint flags, StringBuilder sb, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetApplicationUserModelId(IntPtr p, ref uint len, StringBuilder sb);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern int GetObjectW(IntPtr o, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    // ---------- dwmapi ----------

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

    // ---------- shell32 ----------

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr ILCreateFromPathW(string path);
    [DllImport("shell32.dll")] public static extern void ILFree(IntPtr pidl);
    [DllImport("shell32.dll")] public static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr items, uint flags);
    [DllImport("shell32.dll")] public static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);

    // ---------- константы ----------

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    public const uint GW_OWNER = 4, GA_ROOTOWNER = 3;
    public const int SW_RESTORE = 9, SW_MINIMIZE = 6;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_CLOSE = 0xF060;
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    /// Открыто меню чужой программы (из трея): наши окна стоят прямо под ним и остаются в слое
    /// «поверх всех» — иначе развёрнутое окно программы, вышедшее вперёд при клике, накрывает
    /// док (Claude). Под окно программы, запущенной от администратора (Fan Control, MSI
    /// Afterburner), Windows не пускает (ошибка 5): тогда опускаем наши окна под слой «поверх
    /// всех», как раньше.
    public static bool ForeignMenuOpen { get; private set; }
    static IntPtr _under;
    static bool _belowTopmost;

    // Под слой опущены только наши окна, которые меню перекрывают (и те, что перекрывают их):
    // остальные (док под окошком трея) остаются сверху, и окно программы, вышедшее вперёд, их
    // не накрывает.
    static readonly HashSet<IntPtr> Demoted = [];
    static List<RECT> _menuRects = [];

    /// Окно выбора области для снимка: пока оно открыто, наши окна (док выезжает или прячется)
    /// встают под него, а не поверх замёрзшего экрана.
    public static IntPtr Cover { get; set; }

    /// Куда ставить наше окно: на самый верх или, пока открыто чужое меню, под него.
    public static IntPtr TopZFor(IntPtr hwnd)
    {
        if (Cover != IntPtr.Zero && hwnd != Cover && IsWindow(Cover)) return Cover;
        if (!ForeignMenuOpen) return HWND_TOPMOST;
        if (!_belowTopmost) return IsWindow(_under) ? _under : HWND_TOPMOST;
        return Demoted.Contains(hwnd) || Overlaps(Bounds(hwnd), _menuRects) ? HWND_NOTOPMOST : HWND_TOPMOST;
    }

    /// Поднять наше окно наверх (но не выше открытого чужого меню).
    public static void RaiseTop(IntPtr hwnd) => SetWindowPos(hwnd, TopZFor(hwnd), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// Видимая часть окна на экране, с учётом его области (SetWindowRgn у дока).
    static RECT Bounds(IntPtr h)
    {
        if (!GetWindowRect(h, out var r)) return default;
        var kind = GetWindowRgnBox(h, out var box);
        if (kind is 2 or 3 /* SIMPLEREGION, COMPLEXREGION */)
            return new RECT { Left = r.Left + box.Left, Top = r.Top + box.Top, Right = r.Left + box.Right, Bottom = r.Top + box.Bottom };
        return kind == 1 /* NULLREGION */ ? default : r;
    }

    static bool Overlap(RECT a, RECT b) =>
        a.Right > a.Left && a.Bottom > a.Top && b.Right > b.Left && b.Bottom > b.Top &&
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    static bool Overlaps(RECT a, IEnumerable<RECT> list) => list.Any(b => Overlap(a, b));

    [DllImport("user32.dll")] static extern int GetWindowRgnBox(IntPtr h, out RECT box);

    /// Наши видимые окна сверху вниз.
    static List<IntPtr> OwnWindows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<IntPtr>();
        for (var h = GetWindow(GetDesktopWindow(), 5 /* GW_CHILD */); h != IntPtr.Zero; h = GetWindow(h, 2 /* GW_HWNDNEXT */))
            if (IsWindowVisible(h) && ProcessId(h) == own) list.Add(h);
        return list;
    }

    /// Открылось чужое меню (`menus` — его окна): наши окна, которые выше него, встают прямо под
    /// нижнее из них, порядок между нашими окнами сохраняется (снизу вверх: каждое следующее
    /// встаёт над предыдущими). Повторный вызов опускает окна, которые успели подняться.
    public static int DemoteOwn(IReadOnlyCollection<IntPtr> menus)
    {
        ForeignMenuOpen = true;
        var own = (uint)Environment.ProcessId;
        if (!_belowTopmost)
        {
            // Наши окна выше нижнего окна меню, сверху вниз.
            var seen = new List<IntPtr>();
            var above = 0;
            var under = IntPtr.Zero;
            for (var h = GetWindow(GetDesktopWindow(), 5 /* GW_CHILD */); h != IntPtr.Zero; h = GetWindow(h, 2 /* GW_HWNDNEXT */))
            {
                if (menus.Contains(h)) { under = h; above = seen.Count; }
                else if (IsWindowVisible(h) && ProcessId(h) == own) seen.Add(h);
            }
            // Под окном не из слоя «поверх всех» наши окна потеряли бы этот слой — тогда запасной путь.
            if (under != IntPtr.Zero && (ExStyle(under) & WS_EX_TOPMOST) != 0)
            {
                _under = under;
                var moved = 0;
                for (var i = above - 1; i >= 0; i--, moved++)
                    if (!SetWindowPos(seen[i], under, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)) { _belowTopmost = true; break; }
                if (!_belowTopmost) return moved;
            }
            else _belowTopmost = true;
        }
        // Запасной путь: опускаем под слой только окна под меню и всё, что с ними перекрывается
        // (подложка дока вместе с иконками), порядок между ними сохраняется.
        _menuRects = menus.Select(Bounds).ToList();
        var list = OwnWindows();
        var bounds = list.ToDictionary(h => h, Bounds);
        foreach (var h in list) if (Overlaps(bounds[h], _menuRects)) Demoted.Add(h);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var h in list)
                if (!Demoted.Contains(h) && Overlaps(bounds[h], Demoted.Where(bounds.ContainsKey).Select(d => bounds[d])))
                    grew |= Demoted.Add(h);
        }
        var count = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (!Demoted.Contains(list[i]) || (ExStyle(list[i]) & WS_EX_TOPMOST) == 0) continue;
            SetWindowPos(list[i], HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            count++;
        }
        return count;
    }

    /// Чужое меню закрылось. Наши окна стояли под ним — так и остаются «поверх всех». Если их
    /// опускали под слой, поднимаем разом (DeferWindowPos), в прежнем порядке: по одному док
    /// успевал показать подложку без иконок.
    public static void RestoreOwn()
    {
        if (!ForeignMenuOpen) return;
        ForeignMenuOpen = false;
        _under = IntPtr.Zero;
        if (!_belowTopmost) return;
        _belowTopmost = false;
        Demoted.Clear();
        _menuRects = [];
        var list = OwnWindows();
        var dwp = BeginDeferWindowPos(list.Count);
        for (var i = list.Count - 1; i >= 0 && dwp != IntPtr.Zero; i--)
            dwp = DeferWindowPos(dwp, list[i], HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        if (dwp != IntPtr.Zero) EndDeferWindowPos(dwp);
        else foreach (var h in Enumerable.Reverse(list)) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")] static extern IntPtr BeginDeferWindowPos(int n);
    [DllImport("user32.dll")] static extern IntPtr DeferWindowPos(IntPtr dwp, IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool EndDeferWindowPos(IntPtr dwp);

    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200, SWP_ASYNCWINDOWPOS = 0x4000;
    public const uint MONITOR_DEFAULTTONEAREST = 2, MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17;
    public const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_NAMECHANGE = 0x800C, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    public const int OBJID_WINDOW = 0;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const int DWMWA_CLOAKED = 14, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    public const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    public const int RGN_OR = 2;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_ESCAPE = 0x1B, VK_LWIN = 0x5B;
    public const uint KEYEVENTF_KEYUP = 2;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    public static bool KeyDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    /// «Эффекты анимации» в Windows. Выключены — всё появляется сразу (правило 0.8).
    public static bool AnimationsEnabled()
    {
        return !SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) || on != 0;
    }

    // ---------- структуры ----------

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
        // Место под палитру, которую GetDIBits может дописать за заголовок.
        public uint pad0, pad1, pad2, pad3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags; public RECT rcDestination, rcSource; public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }
    public const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd;
        public string? lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow; public IntPtr hInstApp, lpIDList; public string? lpClass;
        public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess;
    }
    public const uint SEE_MASK_NOASYNC = 0x100, SEE_MASK_FLAG_NO_UI = 0x400;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY { public Guid fmtid; public uint pid; public PROPERTYKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }
    public const ushort VT_LPWSTR = 31;

    public static readonly PROPERTYKEY PKEY_AppUserModel_ID = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
    public static readonly PROPERTYKEY PKEY_ItemNameDisplay = new("B725F130-47EF-101A-A5F1-02608C9EEBAC", 10);
    public static readonly PROPERTYKEY PKEY_Link_TargetParsingPath = new("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25", 2);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }
    public const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
    public const int E_PENDING = unchecked((int)0x8000000A);

    // ---------- помощники ----------

    public static string WindowText(IntPtr h)
    {
        var len = GetWindowTextLengthW(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static long ExStyle(IntPtr h) => (long)GetWindowLongPtrW(h, GWL_EXSTYLE);

    public delegate IntPtr SubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    const uint WM_NCCALCSIZE = 0x0083;

    // Делегат живёт всё время работы программы: окно зовёт его до самого закрытия.
    static readonly SubclassProc NoFrameProc = (h, msg, w, l, _, _) =>
        msg == WM_NCCALCSIZE && w != IntPtr.Zero ? IntPtr.Zero : DefSubclassProc(h, msg, w, l);

    /// Окно без рамки. WinUI без заголовка оставляет WS_DLGFRAME и сам возвращает его после
    /// каждого показа, а рамка в 3 px рисуется белой полоской по краю окна. Поэтому не трогаем
    /// стиль, а говорим Windows, что рамки нет: вся площадь окна — содержимое.
    public static void RemoveFrame(IntPtr h)
    {
        SetWindowSubclass(h, NoFrameProc, (UIntPtr)1, UIntPtr.Zero);
        SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void AddExStyle(IntPtr h, long add, long remove = 0)
    {
        var ex = (ExStyle(h) | add) & ~remove;
        SetWindowLongPtrW(h, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var c, 4) == 0 && c != 0;

    public static uint ProcessId(IntPtr h)
    {
        GetWindowThreadProcessId(h, out var pid);
        return pid;
    }

    /// Путь к exe процесса и AUMID упакованного приложения.
    public static (string? Exe, string? Aumid) ProcessInfo(uint pid)
    {
        var p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return (null, null);
        try
        {
            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            string? exe = QueryFullProcessImageNameW(p, 0, sb, ref len) ? sb.ToString() : null;
            var sb2 = new StringBuilder(1024);
            uint len2 = (uint)sb2.Capacity;
            string? aumid = GetApplicationUserModelId(p, ref len2, sb2) == 0 ? sb2.ToString() : null;
            return (exe, string.IsNullOrEmpty(aumid) ? null : aumid);
        }
        finally { CloseHandle(p); }
    }

    static Guid IID_IPropertyStore = typeof(IPropertyStore).GUID;

    static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try
        {
            if (pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero) return Marshal.PtrToStringUni(pv.p);
            return null;
        }
        finally { PropVariantClear(ref pv); }
    }

    /// AUMID, который окну назначила программа (так группирует и панель задач).
    public static string? WindowAumid(IntPtr h)
    {
        try
        {
            if (SHGetPropertyStoreForWindow(h, ref IID_IPropertyStore, out var store) != 0) return null;
            try { var s = ReadString(store, PKEY_AppUserModel_ID); return string.IsNullOrEmpty(s) ? null : s; }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }

    /// Строковое свойство элемента оболочки (`shell:AppsFolder\…` или путь).
    public static string? ShellProperty(string parsingName, PROPERTYKEY key)
    {
        try
        {
            if (SHGetPropertyStoreFromParsingName(parsingName, IntPtr.Zero, 0, ref IID_IPropertyStore, out var store) != 0) return null;
            try { var s = ReadString(store, key); return string.IsNullOrEmpty(s) ? null : s; }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }

    public static MONITORINFO MonitorInfo(IntPtr mon)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(mon, ref mi);
        return mi;
    }

    public static double MonitorScale(IntPtr mon)
    {
        return GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1.0;
    }

    public static void SetCorner(IntPtr h, int pref)
    {
        DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
    }

    public static void SetBorderColor(IntPtr h, int colorref)
    {
        DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref colorref, 4);
    }

    public static void SetDark(IntPtr h)
    {
        int on = 1;
        DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
    }

    /// Окно появляется и исчезает без системной анимации: свою делаем сами.
    public static void NoTransitions(IntPtr h)
    {
        int on = 1;
        DwmSetWindowAttribute(h, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, 4);
    }
}
