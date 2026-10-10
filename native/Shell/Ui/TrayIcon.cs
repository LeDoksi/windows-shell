using System.Runtime.InteropServices;
using Windows.Foundation;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Ui;

/// Наш значок в трее (раздел 5). Клик или правый клик открывает меню оболочки — наше
/// окошко со стеклом, как все меню. После перезапуска Проводника значок добавляется заново.
public sealed class TrayIcon
{
    const uint Id = 1;
    readonly MessageWindow _window;
    readonly Func<List<MenuItem>> _menu;
    readonly MenuPopup _popup;
    IntPtr _icon;
    bool _added;

    public TrayIcon(MessageWindow window, MenuPopup popup, Func<List<MenuItem>> menu)
    {
        _window = window;
        _popup = popup;
        _menu = menu;
        window.Message += OnMessage;
        window.TaskbarCreated += () => { _added = false; Add(); };
        Add();
    }

    void Add()
    {
        if (_icon == IntPtr.Zero)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "shell.ico");
            var dpi = GetDpiForWindow(_window.Hwnd);
            var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi == 0 ? 96 : dpi);
            _icon = LoadImageW(IntPtr.Zero, path, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        }
        var data = Data();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = MessageWindow.WM_TRAY;
        data.hIcon = _icon;
        data.szTip = "Windows Shell";
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_added)
        {
            // Значок остался от прошлого запуска (оболочка упала) — обновляем его.
            _added = Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
        data.uVersion = 4;
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
        if (!_added) Log.Write("значок в трее не добавился");
    }

    public void Remove()
    {
        var data = Data();
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    NOTIFYICONDATAW Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Hwnd,
        uID = Id,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    const int WM_CONTEXTMENU = 0x007B, WM_LBUTTONUP = 0x0202, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

    void OnMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (msg != MessageWindow.WM_TRAY) return;
        // Версия 4: в младшем слове lParam — событие, в wParam — точка на экране.
        var ev = (int)((long)l & 0xFFFF);
        if (ev is not (WM_CONTEXTMENU or NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONUP)) return;
        if (ev == WM_LBUTTONUP) return; // приходит вместе с NIN_SELECT
        var x = (short)((long)w & 0xFFFF);
        var y = (short)(((long)w >> 16) & 0xFFFF);
        if (!GetCursorPos(out var p)) p = new POINT { X = x, Y = y };
        // Окно меню должно стать активным, иначе клик мимо его не закроет.
        SetForegroundWindow(_window.Hwnd);
        Popups.Toggle(this, () => _popup.Show(this, new Rect(p.X, p.Y, 0, 0), _menu()));
    }

    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
