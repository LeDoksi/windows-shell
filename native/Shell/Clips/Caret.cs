using System.Runtime.InteropServices;
using Windows.Foundation;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Clips;

/// Где текстовый курсор в окне (физические пиксели экрана): окошко Win+V встаёт под ним.
/// Сначала спрашиваем Windows (классический курсор: Блокнот, Word, поля ввода), потом
/// специальные возможности (OBJID_CARET: Chrome, Edge, Telegram, VS Code). Нет — null.
public static class Caret
{
    public static Rect? Find(IntPtr window)
    {
        try
        {
            var thread = GetWindowThreadProcessId(window, out _);
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(thread, ref info)) return null;
            if (info.hwndCaret != IntPtr.Zero && info.rcCaret.Bottom > info.rcCaret.Top)
            {
                var r = info.rcCaret;
                MapWindowPoints(info.hwndCaret, IntPtr.Zero, ref r, 2);
                if (Sane(r)) return new Rect(r.Left, r.Top, Math.Max(1, r.Right - r.Left), r.Bottom - r.Top);
            }
            var focus = info.hwndFocus != IntPtr.Zero ? info.hwndFocus : window;
            return FromAccessible(focus);
        }
        catch (Exception e)
        {
            Log.Error("буфер: текстовый курсор", e);
            return null;
        }
    }

    static Rect? FromAccessible(IntPtr hwnd)
    {
        var iid = typeof(IAccessible).GUID;
        if (AccessibleObjectFromWindow(hwnd, OBJID_CARET, ref iid, out var obj) != 0 || obj is not IAccessible acc) return null;
        try
        {
            if (acc.accLocation(out var x, out var y, out var w, out var h, 0) != 0) return null;
            var r = new RECT { Left = x, Top = y, Right = x + Math.Max(1, w), Bottom = y + h };
            return h > 0 && Sane(r) ? new Rect(x, y, Math.Max(1, w), h) : null;
        }
        finally
        {
            Marshal.ReleaseComObject(acc);
        }
    }

    /// Курсор на каком-то мониторе (бывает 0,0 у окон, где его на самом деле нет).
    static bool Sane(RECT r)
    {
        if (r.Left == 0 && r.Top == 0 && r.Right <= 1) return false;
        var mon = MonitorFromPoint(new POINT { X = r.Left, Y = r.Top }, 0 /* MONITOR_DEFAULTTONULL */);
        return mon != IntPtr.Zero;
    }

    const int OBJID_CARET = -8;

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    /// IAccessible до accLocation; первые четыре — места методов IDispatch.
    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAccessible
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();
        void get_accParent();
        void get_accChildCount();
        void get_accChild();
        void get_accName();
        void get_accValue();
        void get_accDescription();
        void get_accRole();
        void get_accState();
        void get_accHelp();
        void get_accHelpTopic();
        void get_accKeyboardShortcut();
        void get_accFocus();
        void get_accSelection();
        void get_accDefaultAction();
        void accSelect();
        [PreserveSig] int accLocation(out int left, out int top, out int width, out int height, [MarshalAs(UnmanagedType.Struct)] object child);
    }

    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT points, int count);
    [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr hwnd, int id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
}
