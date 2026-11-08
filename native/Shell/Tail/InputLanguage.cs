using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Tail;

/// Раскладка клавиатуры в списке.
public sealed record Layout(IntPtr Hkl, string Short, string Name);

/// Язык ввода (раздел 8, WSH-81): «RU» / «EN» в хвосте, меняется сразу при Alt+Shift и
/// Win+Пробел. Отдельного события у Windows нет, поэтому фоновый поток раз в 250 мс смотрит
/// раскладку активного окна (одна пара вызовов, нагрузки нет) и сообщает только о смене.
public sealed class InputLanguage
{
    readonly DispatcherQueue _queue;
    IntPtr _hkl;

    public Layout Current { get; private set; }
    public event Action? Changed;

    public InputLanguage(DispatcherQueue queue)
    {
        _queue = queue;
        _hkl = Foreground();
        Current = Describe(_hkl);
        new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(250);
                var h = Foreground();
                if (h == IntPtr.Zero || h == _hkl) continue;
                _hkl = h;
                var layout = Describe(h);
                _queue.TryEnqueue(() => { Current = layout; Changed?.Invoke(); });
            }
        }) { IsBackground = true, Name = "input-language" }.Start();
    }

    /// Раскладка потока активного окна (у консоли бывает 0 — тогда не трогаем).
    static IntPtr Foreground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return IntPtr.Zero;
        // Наше окошко активно (меню языка): показываем раскладку окна, которое было до него.
        if (ProcessId(fg) == (uint)Environment.ProcessId) return IntPtr.Zero;
        return GetKeyboardLayout(GetWindowThreadProcessId(fg, out _));
    }

    public static List<Layout> All()
    {
        var n = GetKeyboardLayoutList(0, null);
        var list = new IntPtr[Math.Max(n, 0)];
        if (n > 0) GetKeyboardLayoutList(n, list);
        return list.Select(Describe).ToList();
    }

    static Layout Describe(IntPtr hkl)
    {
        var lang = (int)((long)hkl & 0xFFFF);
        try
        {
            var c = CultureInfo.GetCultureInfo(lang);
            var name = c.NativeName;
            if (name.Length > 0) name = char.ToUpper(name[0]) + name[1..];
            return new Layout(hkl, c.TwoLetterISOLanguageName.ToUpperInvariant(), name);
        }
        catch { return new Layout(hkl, "??", $"Раскладка {lang:X4}"); }
    }

    /// Переключить раскладку окна, в котором работаешь (как выбор в списке Windows).
    public void Select(IntPtr window, Layout layout)
    {
        if (window != IntPtr.Zero) PostMessageW(window, 0x0050 /* WM_INPUTLANGCHANGEREQUEST */, IntPtr.Zero, layout.Hkl);
        _hkl = layout.Hkl;
        Current = layout;
        Changed?.Invoke();
    }

    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] static extern int GetKeyboardLayoutList(int n, IntPtr[]? list);
}
