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

/// Уведомления (раздел 8, WSH-82): колокольчик у часов. Точка — есть новые с прошлого открытия
/// центра уведомлений; полумесяц — режим «Не беспокоить». Клик открывает центр уведомлений
/// Windows (как Win+N). Новые уведомления читаем через UserNotificationListener, если Windows
/// разрешит; иначе точку зажигают всплывающие подсказки программ из трея.
public sealed class Notifications
{
    readonly DispatcherQueue _queue;
    HashSet<uint> _seen = [];
    bool _listener;

    public bool Unread { get; private set; }
    public bool DoNotDisturb { get; private set; }
    public event Action? Changed;

    public Notifications(DispatcherQueue queue)
    {
        _queue = queue;
        _ = StartListener();
        new Thread(() =>
        {
            // «Не беспокоить» — дёшево, раз в 2 с; список уведомлений (WinRT) — раз в 10 с.
            for (var tick = 0; ; tick++)
            {
                var dnd = QuietMode();
                if (dnd != DoNotDisturb) _queue.TryEnqueue(() => { DoNotDisturb = dnd; Changed?.Invoke(); });
                if (_listener && tick % 5 == 0) _ = Poll();
                Thread.Sleep(2000);
            }
        }) { IsBackground = true, Name = "notifications" }.Start();
    }

    async Task StartListener()
    {
        try
        {
            var l = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            var access = await l.RequestAccessAsync();
            _listener = access == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed;
            Log.Write($"уведомления: доступ {access}");
            if (_listener) _seen = await Ids();
        }
        catch (Exception e)
        {
            Log.Write($"уведомления: список недоступен ({e.Message})");
        }
    }

    static async Task<HashSet<uint>> Ids()
    {
        var list = await Windows.UI.Notifications.Management.UserNotificationListener.Current.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast);
        return list.Select(n => n.Id).ToHashSet();
    }

    async Task Poll()
    {
        try
        {
            var ids = await Ids();
            var unread = ids.Any(id => !_seen.Contains(id));
            if (unread != Unread) _queue.TryEnqueue(() => { Unread = unread; Changed?.Invoke(); });
        }
        catch { }
    }

    /// Программа из трея показала подсказку.
    public void NoteBalloon()
    {
        if (Unread) return;
        Unread = true;
        Changed?.Invoke();
    }

    /// Открыли центр уведомлений: всё прочитано.
    public void MarkRead()
    {
        if (_listener) _ = Task.Run(async () => { try { _seen = await Ids(); } catch { } });
        Unread = false;
        Changed?.Invoke();
    }

    // «Не беспокоить»: состояние WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED (0 — выключено).
    static bool QuietMode()
    {
        try
        {
            var name = 0x0D83063EA3BF1C75UL;
            var buf = new byte[4];
            var size = 4u;
            return NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, buf, ref size) == 0 && size >= 4 && BitConverter.ToInt32(buf) != 0;
        }
        catch { return false; }
    }

    [DllImport("ntdll.dll")] static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr scope, out uint changeStamp, byte[] buffer, ref uint size);
}
