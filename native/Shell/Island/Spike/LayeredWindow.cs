using System.Drawing;
using System.Runtime.InteropServices;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Island.Spike;

/// Слоистое окно поверх всех, сквозное для мыши и без фокуса (как Perf/Overlay): картинка
/// с прозрачностью через UpdateLayeredWindow. Пробам нужно для кромки (вариант b2) и пилюли записи.
internal sealed class LayeredWindow : IDisposable
{
    const string ClassName = "WindowsShellNative.IslandLayer";
    static WndProc? _proc;
    static bool _registered;

    readonly bool _excludeFromCapture;
    IntPtr _hwnd;
    bool _shown;

    public LayeredWindow(bool excludeFromCapture) => _excludeFromCapture = excludeFromCapture;

    public IntPtr Hwnd => _hwnd;
    public bool Visible => _shown;

    public void Show(Bitmap bmp, int x, int y)
    {
        Create();
        Push(bmp, x, y);
        if (!_shown)
        {
            ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
            _shown = true;
        }
    }

    /// Поднять над другими окнами «поверх всех» (игра тоже бывает поверх всех).
    public void Raise()
    {
        if (_hwnd != IntPtr.Zero) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Hide()
    {
        if (!_shown) return;
        ShowWindow(_hwnd, 0 /* SW_HIDE */);
        _shown = false;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _shown = false;
    }

    void Create()
    {
        if (_hwnd != IntPtr.Zero) return;
        if (!_registered)
        {
            _proc = DefWindowProcW;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            RegisterClassExW(ref wc);
            _registered = true;
        }
        _hwnd = CreateWindowExW((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */,
            ClassName, "Остров", 0x80000000 /* WS_POPUP */, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        // Не попадает в снимки и запись экрана (своя пилюля записи не должна оказаться в видео).
        if (_excludeFromCapture) SetWindowDisplayAffinity(_hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
    }

    void Push(Bitmap bmp, int x, int y)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        try
        {
            var dst = new POINT { X = x, Y = y };
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE size, IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
