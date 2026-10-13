using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace WindowsShell.Native;

/// Клавиша Win и наши сочетания с ней (разделы 3.1 и 6) через низкоуровневый хук клавиатуры.
///
/// Win нажали и отпустили без других клавиш — открывается наш Пуск. Чтобы родной Пуск при этом
/// не открылся, отпускание Win не пропускаем, а вместо него отправляем ничего не значащую клавишу
/// (vkE8, как делает AutoHotkey) и только потом отпускание Win: Windows видит «Win + клавиша»
/// и свой Пуск не открывает. Win+E, Win+D и прочие сочетания проходят как есть.
///
/// Хук живёт в своём потоке с очередью сообщений: Windows снимает хук, который отвечает
/// медленно, а поток интерфейса бывает занят. В поток интерфейса уходят только события.
public sealed class WinKeys
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const uint LLKHF_INJECTED = 0x10;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_MASK = 0xE8;
    const uint WM_QUIT = 0x12;

    readonly DispatcherQueue _queue;
    readonly HookProc _proc;
    uint _thread;

    // Только в потоке хука.
    bool _winDown;
    bool _combo;
    // Сочетание забрали себе: Windows видела только Win и на отпускании открыла бы свой Пуск.
    bool _swallowedCombo;
    readonly HashSet<int> _swallowUp = [];

    /// Клавиши-стрелки уходят доку (Win+T, Win+B), а не активному окну.
    public volatile bool Capturing;

    /// Win нажали и отпустили одну.
    public event Action? Start;

    /// Наше сочетание: клавиша (VK), Shift, Ctrl. Win+X, Win+S, Win+Q, Win+T, Win+B, Win+1…9.
    public event Action<int, bool, bool>? Combo;

    /// Клавиша для дока, пока он в режиме клавиатуры: VK и Shift.
    public event Action<int, bool>? Captured;

    /// Режим клавиатуры дока закончился другой клавишей.
    public event Action? CaptureBroken;

    public WinKeys(DispatcherQueue queue)
    {
        _queue = queue;
        _proc = Proc;
        var t = new Thread(Run) { IsBackground = true, Name = "winkeys" };
        t.Start();
    }

    void Run()
    {
        _thread = GetCurrentThreadId();
        var hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (hook == IntPtr.Zero) { Log.Write($"хук клавиатуры не встал: {Marshal.GetLastWin32Error()}"); return; }
        Log.Write("хук клавиатуры: Win теперь открывает наш Пуск");
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        UnhookWindowsHookEx(hook);
    }

    /// Снять хук (выход): Win снова открывает родной Пуск.
    public void Stop()
    {
        if (_thread != 0) PostThreadMessageW(_thread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// Наши сочетания с Win. Остальные (Win+E, Win+D, Win+L…) пропускаем Windows.
    static bool Ours(int vk, bool shift, bool ctrl)
    {
        if (vk is >= 0x31 and <= 0x39) return !ctrl || shift; // Win+цифра, Win+Shift+цифра, Win+Ctrl+Shift+цифра
        if (shift || ctrl) return false;
        return vk is 'X' or 'S' or 'Q' or 'T' or 'B';
    }

    /// Клавиши, которыми ходят по доку в режиме клавиатуры.
    static bool NavKey(int vk) => vk is 0x25 or 0x26 or 0x27 or 0x28 /* стрелки */ or 0x0D /* Enter */ or 0x20 /* пробел */
        or 0x1B /* Esc */ or 0x5D /* клавиша меню */ or 0x79 /* F10 */ or 0x09 /* Tab */ or 0x24 or 0x23 /* Home, End */;

    IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, w, l);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(l);
            // Свои же нажатия (маска, Win+D из меню) не разбираем. Чужие программные нажатия
            // (AutoHotkey, удалённый рабочий стол, макросы клавиатуры) разбираем как обычные.
            if ((k.flags & LLKHF_INJECTED) != 0 && k.dwExtraInfo == Mark) return CallNextHookEx(IntPtr.Zero, code, w, l);
            var vk = (int)k.vkCode;
            var down = (int)w is WM_KEYDOWN or WM_SYSKEYDOWN;

            if (vk is VK_LWIN or VK_RWIN)
            {
                if (down)
                {
                    // Автоповтор, пока держат, — не новое нажатие.
                    if (!_winDown) { _winDown = true; _combo = false; _swallowedCombo = false; }
                    if (Capturing) { Capturing = false; Post(() => CaptureBroken?.Invoke()); }
                    return CallNextHookEx(IntPtr.Zero, code, w, l);
                }
                var other = vk == VK_LWIN ? VK_RWIN : VK_LWIN;
                var alone = _winDown && !_combo && !Down(other);
                if (!Down(other)) _winDown = false;
                if (!alone)
                {
                    if (!_swallowedCombo || _winDown) return CallNextHookEx(IntPtr.Zero, code, w, l);
                    _swallowedCombo = false;
                    SendMaskAndWinUp(vk, k.scanCode);
                    return 1;
                }
                SendMaskAndWinUp(vk, k.scanCode);
                Post(() => Start?.Invoke());
                return 1;
            }

            if (!down && _swallowUp.Remove(vk)) return 1;

            if (_winDown)
            {
                if (!down) return CallNextHookEx(IntPtr.Zero, code, w, l);
                _combo = true;
                var shift = Down(VK_SHIFT);
                var ctrl = Down(VK_CONTROL);
                if (!Down(VK_MENU) && Ours(vk, shift, ctrl))
                {
                    _swallowUp.Add(vk);
                    _swallowedCombo = true;
                    Post(() => Combo?.Invoke(vk, shift, ctrl));
                    return 1;
                }
                return CallNextHookEx(IntPtr.Zero, code, w, l);
            }

            if (Capturing && down)
            {
                if (NavKey(vk))
                {
                    var shift = Down(VK_SHIFT);
                    // Shift+F10 = правый клик; просто F10 и прочее Windows оставляем.
                    if (vk != 0x79 || shift)
                    {
                        _swallowUp.Add(vk);
                        Post(() => Captured?.Invoke(vk, shift));
                        return 1;
                    }
                }
                if (vk is not (VK_SHIFT or 0xA0 or 0xA1))
                {
                    Capturing = false;
                    Post(() => CaptureBroken?.Invoke());
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("хук клавиатуры", e);
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    void Post(Action a) => _queue.TryEnqueue(() =>
    {
        try { a(); }
        catch (Exception e) { Log.Error("клавиша Win", e); }
    });

    static void SendMaskAndWinUp(int winVk, uint scan)
    {
        var inputs = new INPUT[]
        {
            Key(VK_MASK, 0, 0),
            Key(VK_MASK, 0, KEYEVENTF_KEYUP),
            Key(winVk, (ushort)scan, KEYEVENTF_KEYUP | KEYEVENTF_EXTENDEDKEY),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// Нажать и отпустить сочетание с Win от нашего имени (пункт «Рабочий стол» = Win+D).
    public static void SendWinCombo(int vk)
    {
        var inputs = new INPUT[]
        {
            Key(VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY),
            Key(vk, 0, 0),
            Key(vk, 0, KEYEVENTF_KEYUP),
            Key(VK_LWIN, 0, KEYEVENTF_KEYUP | KEYEVENTF_EXTENDEDKEY),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// Ничего не значащее нажатие от нашего имени: после него Windows разрешает нам вывести
    /// своё окно вперёд (Пуск по клавише Win).
    public static void SendMask()
    {
        var inputs = new INPUT[] { Key(VK_MASK, 0, 0), Key(VK_MASK, 0, KEYEVENTF_KEYUP) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// Метка наших нажатий (dwExtraInfo), чтобы хук узнавал их среди программных.
    static readonly IntPtr Mark = new(0x57534845); // «WSHE»

    static INPUT Key(int vk, ushort scan, uint flags) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = flags, dwExtraInfo = Mark },
    };

    const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;

    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    // INPUT в x64: тип (4 байта + выравнивание) и объединение размером с MOUSEINPUT (32 байта).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public int type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
