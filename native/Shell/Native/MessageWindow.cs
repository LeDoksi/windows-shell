using System.Runtime.InteropServices;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Native;

/// Невидимое окно для сообщений Windows, которые приходят только окнам: значок в трее,
/// горячие клавиши, «окно просит внимания», смена мониторов, сон и перезапуск Проводника.
/// Обычное окно верхнего уровня (не HWND_MESSAGE): широковещательные сообщения вроде
/// WM_DISPLAYCHANGE до окон-только-для-сообщений не доходят. Никогда не показывается.
public sealed class MessageWindow
{
    public const uint WM_TRAY = 0x8000 + 1; // WM_APP + 1

    readonly WndProc _proc;
    readonly uint _shellHookMsg;
    readonly uint _taskbarCreated;
    public IntPtr Hwnd { get; }

    /// Любое сообщение (msg, wParam, lParam). Обработчики вызываются в потоке интерфейса.
    public event Action<uint, IntPtr, IntPtr>? Message;

    /// Окно мигает в панели задач (просит внимания).
    public event Action<IntPtr>? Flash;

    /// Мониторы, разрешение, масштаб или рабочая область поменялись.
    public event Action? DisplayChanged;

    /// Компьютер проснулся.
    public event Action? Resumed;

    /// Проводник перезапустился: значок в трее нужно добавить заново.
    public event Action? TaskbarCreated;

    public event Action<int>? Hotkey;

    public MessageWindow()
    {
        _proc = Proc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "WindowsShellNative.Messages",
        };
        RegisterClassExW(ref wc);
        Hwnd = CreateWindowExW((uint)WS_EX_TOOLWINDOW, wc.lpszClassName, "Windows Shell", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _shellHookMsg = RegisterWindowMessageW("SHELLHOOK");
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        RegisterShellHookWindow(Hwnd);
        // Мы не администратор, но Проводник может быть: пропускаем его сообщение о перезапуске.
        ChangeWindowMessageFilterEx(Hwnd, _taskbarCreated, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        ChangeWindowMessageFilterEx(Hwnd, WM_TRAY, 1, IntPtr.Zero);
    }

    const uint WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A, WM_DPICHANGED = 0x02E0, WM_POWERBROADCAST = 0x0218, WM_HOTKEY = 0x0312;
    const int PBT_APMRESUMEAUTOMATIC = 0x12, PBT_APMRESUMESUSPEND = 0x7;
    const int HSHELL_FLASH = 0x8006, HSHELL_RUDEAPPACTIVATED = 0x8004;
    const uint SPI_SETWORKAREA = 0x2F;

    IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (msg == _shellHookMsg)
            {
                if ((int)w == HSHELL_FLASH) Flash?.Invoke(l);
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0) { TaskbarCreated?.Invoke(); return IntPtr.Zero; }
            switch (msg)
            {
                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                    DisplayChanged?.Invoke();
                    break;
                case WM_SETTINGCHANGE when (uint)w == SPI_SETWORKAREA:
                    DisplayChanged?.Invoke();
                    break;
                case WM_POWERBROADCAST when (int)w is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND:
                    Resumed?.Invoke();
                    break;
                case WM_HOTKEY:
                    Hotkey?.Invoke((int)w);
                    break;
            }
            Message?.Invoke(msg, w, l);
        }
        catch (Exception e)
        {
            Log.Error($"сообщение 0x{msg:X}", e);
        }
        return DefWindowProcW(h, msg, w, l);
    }

    public delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
