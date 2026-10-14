using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Tray;

/// Иконка фоновой программы в трее.
public sealed record TrayItem(
    IntPtr Hwnd,
    uint Uid,
    Guid Guid,
    uint Callback,
    uint Version,
    string Tip,
    TrayPixels? Icon,
    bool Hidden,
    long Seq);

/// Картинка иконки: BGRA с умноженной альфой, сверху вниз (как ждёт WriteableBitmap).
public sealed record TrayPixels(int Width, int Height, byte[] Data);

/// Свой трей (WSH-46) по схеме ManagedShell/Cairo, проверенной спайком WSH-44: своё окно класса
/// `Shell_TrayWnd` поверх окна Проводника. `Shell_NotifyIcon` находит первым его и шлёт иконки
/// нам (WM_COPYDATA с SHELLTRAYDATA); мы запоминаем иконку и пересылаем то же сообщение Проводнику,
/// так что его трей продолжает работать как раньше (уведомления, «Параметры → Панель задач»).
/// Заодно ловим сообщения ITaskbarList (полоска загрузки и значок поверх иконки программы):
/// программы шлют их окну из свойства «TaskbandHWND» верхнего Shell_TrayWnd.
/// Всё работает в своём потоке; в поток интерфейса уходят только готовые перемены.
public sealed class TrayHost
{
    readonly DispatcherQueue _queue;
    readonly Dictionary<(IntPtr, uint), TrayItem> _items = [];
    readonly object _gate = new();
    readonly WndProc _trayProc, _bandProc;
    Thread? _thread;
    IntPtr _tray, _notify, _band;
    long _seq;
    uint _taskbarButtonCreated;

    /// Иконки поменялись (в потоке интерфейса).
    public event Action? Changed;

    /// Программа показала всплывающую подсказку (NIF_INFO): на ⌃ появляется точка.
    public event Action? Balloon;

    /// Полоска загрузки окна: состояние (0 нет, 1 бегает, 2 обычная, 4 ошибка, 8 пауза) и доля 0…1.
    public event Action<IntPtr, int, double>? Progress;

    /// Значок поверх иконки окна (непрочитанные и т. п.), null — убрали.
    public event Action<IntPtr, TrayPixels?>? Overlay;

    /// Где наш ⌃ на экране: программы спрашивают место своей иконки (Shell_NotifyIconGetRect).
    public RECT Anchor { get; set; }

    public bool Running => _tray != IntPtr.Zero;

    public TrayHost(DispatcherQueue queue)
    {
        _queue = queue;
        _trayProc = TrayProc;
        _bandProc = BandProc;
    }

    public IReadOnlyList<TrayItem> Items
    {
        get { lock (_gate) return _items.Values.OrderBy(i => i.Seq).ToList(); }
    }

    public void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(3000);
    }

    /// Закрыть наши окна (синхронно): дальше иконки идут прямо Проводнику.
    public void Stop()
    {
        if (_tray == IntPtr.Zero) return;
        SendMessageTimeoutW(_tray, WM_CLOSE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out _);
        Log.Write("трей: наше окно закрыто");
    }

    /// Окна, которым надо сказать, что у них есть «кнопка панели задач»: только после этого
    /// программы шлют полоску загрузки и значки (как делает Проводник).
    public void AnnounceButtons(IEnumerable<IntPtr> windows)
    {
        if (_band == IntPtr.Zero || _taskbarButtonCreated == 0) return;
        foreach (var h in windows) SendNotifyMessageW(h, _taskbarButtonCreated, IntPtr.Zero, IntPtr.Zero);
    }

    void Run(ManualResetEventSlim ready)
    {
        try
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4) /* PER_MONITOR_AWARE_V2 */);
            var inst = GetModuleHandleW(null);
            Register("Shell_TrayWnd", _trayProc, inst);
            Register("TrayNotifyWnd", _trayProc, inst);
            Register("WindowsShellNative.Taskband", _bandProc, inst);
            _tray = CreateWindowExW(WS_EX_TOPMOST_U | WS_EX_TOOLWINDOW_U, "Shell_TrayWnd", "", WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_tray == IntPtr.Zero) { Log.Write("трей: окно не создалось"); return; }
            // Некоторые программы ищут место трея по дочернему окну TrayNotifyWnd.
            _notify = CreateWindowExW(0, "TrayNotifyWnd", null, WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS, 0, 0, 0, 0, _tray, IntPtr.Zero, inst, IntPtr.Zero);
            _band = CreateWindowExW(WS_EX_TOOLWINDOW_U, "WindowsShellNative.Taskband", "", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            _taskbarButtonCreated = RegisterWindowMessageW("TaskbarButtonCreated");
            // Мы не администратор, а программы могут быть: пропускаем их сообщения к нам.
            foreach (var h in new[] { _tray, _band })
            {
                ChangeWindowMessageFilterEx(h, WM_COPYDATA, 1, IntPtr.Zero);
                for (uint m = WM_USER + 50; m <= WM_USER + 90; m++) ChangeWindowMessageFilterEx(h, m, 1, IntPtr.Zero);
            }
            SetProp(_tray, "TaskbandHWND", _band);
            Raise();
            SetTimer(_tray, 1, 500, IntPtr.Zero);
            Log.Write($"трей: наше окно 0x{_tray:X}, окно Проводника 0x{Taskbar.ExplorerTray():X}");
            // Программы заново добавляют свои иконки — теперь уже через нас.
            SendNotifyMessageW(HWND_BROADCAST, RegisterWindowMessageW("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception e)
        {
            Log.Error("трей", e);
            return;
        }
        finally { ready.Set(); }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        Log.Write("трей: поток остановлен");
    }

    static void Register(string cls, WndProc proc, IntPtr inst)
    {
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x8 /* CS_DBLCLKS */,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = inst,
            lpszClassName = cls,
        };
        RegisterClassExW(ref wc);
    }

    /// Наше окно трея держим выше окна Проводника: Shell_NotifyIcon берёт первое по порядку.
    void Raise()
    {
        if (_tray == IntPtr.Zero) return;
        if (FindWindowW("Shell_TrayWnd", null) == _tray) return;
        SetWindowPos(_tray, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ---------- окно трея ----------

    IntPtr TrayProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            switch (msg)
            {
                case WM_COPYDATA when l != IntPtr.Zero:
                {
                    var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(l);
                    if (cds.dwData == 1 && cds.lpData != IntPtr.Zero && cds.cbData >= 8)
                    {
                        var data = new byte[cds.cbData];
                        Marshal.Copy(cds.lpData, data, 0, data.Length);
                        if (TrayData.Parse(data) is { } n) Apply(n);
                    }
                    else if (cds.dwData == 3 && cds.lpData != IntPtr.Zero && cds.cbData >= 40)
                    {
                        // Shell_NotifyIconGetRect: программа хочет знать, где её иконка (чтобы
                        // поставить своё окошко рядом). Отвечаем местом ⌃.
                        var what = Marshal.ReadInt32(cds.lpData, 4);
                        var a = Anchor;
                        if (a.Right > a.Left)
                            return what == 1 ? MakeLParam(a.Left, a.Top) : MakeLParam(a.Right, a.Bottom);
                    }
                    return Forward(h, msg, w, l);
                }
                case WM_TIMER:
                    Raise();
                    return IntPtr.Zero;
                case WM_WINDOWPOSCHANGED:
                    // Окно никогда не показываем, даже если кто-то попросит.
                    if (h == _tray && IsWindowVisible(_tray)) ShowWindow(_tray, 0);
                    break;
                case WM_CLOSE when h == _tray:
                    DestroyWindow(_band);
                    DestroyWindow(_tray);
                    return IntPtr.Zero;
                case WM_DESTROY when h == _tray:
                    _tray = IntPtr.Zero;
                    _band = IntPtr.Zero;
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            // Всё прочее, что программы шлют «панели задач», отдаём Проводнику, как ManagedShell.
            if (msg >= WM_USER || msg is WM_COMMAND or WM_ACTIVATEAPP) return Forward(h, msg, w, l);
        }
        catch (Exception e)
        {
            Log.Error($"трей: сообщение 0x{msg:X}", e);
        }
        return DefWindowProcW(h, msg, w, l);
    }

    /// Переслать окну трея Проводника и вернуть его ответ.
    IntPtr Forward(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        var explorer = Taskbar.ExplorerTray();
        if (explorer == IntPtr.Zero) return msg == WM_COPYDATA ? new IntPtr(1) : DefWindowProcW(h, msg, w, l);
        // WM_USER+372 Проводник ждёт через Post (так у ManagedShell), иначе бывают зависания.
        if (msg == WM_USER + 372) { PostMessageW(explorer, msg, w, l); return IntPtr.Zero; }
        SendMessageTimeoutW(explorer, msg, w, l, SMTO_ABORTIFHUNG, 2000, out var result);
        return result;
    }

    // Иконки, которые мы показываем сами: громкость, сеть и питание Проводника.
    static readonly HashSet<Guid> OwnSystem =
    [
        new("7820ae73-23e3-4229-82c1-e41cb67d5b9c"), // громкость
        new("7820ae74-23e3-4229-82c1-e41cb67d5b9c"), // сеть
        new("7820ae75-23e3-4229-82c1-e41cb67d5b9c"), // питание
    ];

    void Apply(TrayData n)
    {
        var balloon = false;
        lock (_gate)
        {
            var key = (n.Hwnd, n.Uid);
            // Иконку могут менять по GUID без окна.
            if (n.Hwnd == IntPtr.Zero && n.Guid != Guid.Empty)
                key = _items.Keys.FirstOrDefault(k => _items[k].Guid == n.Guid, key);
            _items.TryGetValue(key, out var old);
            switch (n.Message)
            {
                case TrayData.NIM_ADD or TrayData.NIM_MODIFY:
                {
                    if (old is null && n.Hwnd == IntPtr.Zero) return;
                    var guid = (n.Flags & TrayData.NIF_GUID) != 0 ? n.Guid : old?.Guid ?? Guid.Empty;
                    if (OwnSystem.Contains(guid)) return;
                    var item = old ?? new TrayItem(n.Hwnd, n.Uid, guid, 0, 0, "", null, false, ++_seq);
                    if ((n.Flags & TrayData.NIF_MESSAGE) != 0) item = item with { Callback = n.Callback };
                    if ((n.Flags & TrayData.NIF_TIP) != 0) item = item with { Tip = n.Tip };
                    if ((n.Flags & TrayData.NIF_ICON) != 0) item = item with { Icon = n.Icon == IntPtr.Zero ? null : IconPixels.From(n.Icon) ?? item.Icon };
                    if ((n.Flags & TrayData.NIF_STATE) != 0 && (n.StateMask & TrayData.NIS_HIDDEN) != 0) item = item with { Hidden = (n.State & TrayData.NIS_HIDDEN) != 0 };
                    if (n.Version is > 0 and <= 4) item = item with { Version = n.Version };
                    item = item with { Guid = guid };
                    _items[key] = item;
                    balloon = (n.Flags & TrayData.NIF_INFO) != 0 && !string.IsNullOrWhiteSpace(n.Info);
                    break;
                }
                case TrayData.NIM_DELETE:
                    if (!_items.Remove(key)) return;
                    break;
                case TrayData.NIM_SETVERSION:
                    if (old is null) return;
                    _items[key] = old with { Version = n.Version };
                    break;
                default:
                    return;
            }
        }
        _queue.TryEnqueue(() =>
        {
            Changed?.Invoke();
            if (balloon) Balloon?.Invoke();
        });
    }

    /// Убрать иконки программ, которые завершились, не попрощавшись (упали).
    public void Prune()
    {
        bool removed;
        lock (_gate)
        {
            var dead = _items.Where(kv => !IsWindow(kv.Value.Hwnd)).Select(kv => kv.Key).ToList();
            foreach (var k in dead) _items.Remove(k);
            removed = dead.Count > 0;
        }
        if (removed) Changed?.Invoke();
    }

    // ---------- клики по иконке (как Проводник) ----------

    const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_RBUTTONDBLCLK = 0x206, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208,
        WM_CONTEXTMENU = 0x7B, NIN_SELECT = 0x400, NIN_POPUPOPEN = 0x406, NIN_POPUPCLOSE = 0x407;

    public enum Button { Left, Right, Middle }

    /// Нажатие на иконку. `x`, `y` — центр иконки на экране (программы ставят там своё меню).
    public static void Down(TrayItem it, Button button, bool doubleClick, int x, int y)
    {
        // Программе надо вывести своё меню или окно вперёд.
        AllowSetForegroundWindow(ProcessId(it.Hwnd));
        var m = button switch
        {
            Button.Left => doubleClick ? WM_LBUTTONDBLCLK : WM_LBUTTONDOWN,
            Button.Right => doubleClick ? WM_RBUTTONDBLCLK : WM_RBUTTONDOWN,
            _ => WM_MBUTTONDOWN,
        };
        Send(it, m, x, y);
    }

    public static void Up(TrayItem it, Button button, int x, int y)
    {
        AllowSetForegroundWindow(ProcessId(it.Hwnd));
        switch (button)
        {
            case Button.Left:
                Send(it, WM_LBUTTONUP, x, y);
                // Документировано для версии 4, но Проводник шлёт и версии 3.
                if (it.Version >= 3) Send(it, NIN_SELECT, x, y);
                break;
            case Button.Right:
                Send(it, WM_RBUTTONUP, x, y);
                if (it.Version >= 3) Send(it, WM_CONTEXTMENU, x, y);
                break;
            default:
                Send(it, WM_MBUTTONUP, x, y);
                break;
        }
    }

    public static void Hover(TrayItem it, bool enter, int x, int y)
    {
        Send(it, WM_MOUSEMOVE, x, y);
        if (it.Version >= 4) Send(it, enter ? NIN_POPUPOPEN : NIN_POPUPCLOSE, x, y);
    }

    /// Версия 4: wParam — точка (x, y), lParam — (событие, uID). Старые: wParam — uID, lParam — событие.
    static void Send(TrayItem it, uint ev, int x, int y)
    {
        if (it.Callback == 0) return;
        IntPtr w, l;
        if (it.Version >= 4)
        {
            w = MakeLParam(x, y);
            l = new IntPtr((nint)((ev & 0xFFFF) | ((it.Uid & 0xFFFF) << 16)));
        }
        else
        {
            w = new IntPtr((nint)it.Uid);
            l = new IntPtr((nint)ev);
        }
        SendNotifyMessageW(it.Hwnd, it.Callback, w, l);
    }

    static IntPtr MakeLParam(int lo, int hi) => new((nint)(uint)((lo & 0xFFFF) | ((hi & 0xFFFF) << 16)));

    // ---------- ITaskbarList: полоска загрузки и значок ----------

    IntPtr BandProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            switch (msg)
            {
                case WM_USER + 64:
                {
                    // SetProgressValue: доля в 0…0xFFFE.
                    var hwnd = w;
                    var value = Math.Clamp((long)l / 65534.0, 0, 1);
                    _queue.TryEnqueue(() => Progress?.Invoke(hwnd, -1, value));
                    return IntPtr.Zero;
                }
                case WM_USER + 65:
                {
                    var hwnd = w;
                    var state = (int)(long)l;
                    _queue.TryEnqueue(() => Progress?.Invoke(hwnd, state, -1));
                    return IntPtr.Zero;
                }
                case WM_USER + 79:
                {
                    // SetOverlayIcon: хэндл иконки общий на сессию, но программа может его
                    // скоро удалить — картинку снимаем сразу.
                    var hwnd = w;
                    var px = l == IntPtr.Zero ? null : IconPixels.From(l);
                    _queue.TryEnqueue(() => Overlay?.Invoke(hwnd, px));
                    return IntPtr.Zero;
                }
                case >= WM_USER + 50 and <= WM_USER + 90:
                    // Остальное (вкладки, кнопки на миниатюре) пока не показываем.
                    return IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            Log.Error($"трей: ITaskbarList 0x{msg:X}", e);
        }
        return DefWindowProcW(h, msg, w, l);
    }

    // ---------- Win32 ----------

    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    const uint WM_USER = 0x400, WM_COPYDATA = 0x4A, WM_TIMER = 0x113, WM_CLOSE = 0x10, WM_DESTROY = 0x2,
        WM_WINDOWPOSCHANGED = 0x47, WM_COMMAND = 0x111, WM_ACTIVATEAPP = 0x1C;
    const uint WS_POPUP = 0x80000000, WS_CHILD = 0x40000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    const uint WS_EX_TOPMOST_U = 0x8, WS_EX_TOOLWINDOW_U = 0x80;
    const uint SMTO_ABORTIFHUNG = 2;
    static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [StructLayout(LayoutKind.Sequential)]
    struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

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

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern bool SendNotifyMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr h, string name, IntPtr data);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr proc);
    static UIntPtr SetTimer(IntPtr h, int id, uint ms, IntPtr proc) => SetTimer(h, (UIntPtr)id, ms, proc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
