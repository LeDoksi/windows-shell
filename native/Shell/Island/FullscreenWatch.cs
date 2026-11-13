using System.Runtime.InteropServices;
using WindowsShell.Island.Core;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Island;

/// Fullscreen по каждому монитору (И2, правки из E22): полное окно запоминается за своим
/// монитором, пока его не закроют, не свернут или там не выйдет вперёд обычное окно; эксклюзив
/// принадлежит одному окну-игре; Пуск, поиск, переключатель задач Windows ничего не меняют.
/// Одно на всю оболочку (WSH-167): док, остров, заряд и «Монитор ПК» спрашивают его. Раньше у
/// дока было своё правило (только окно в фокусе), и клик на второй монитор «снимал» игру с
/// первого — док выезжал над игрой. Зовётся из фонового потока Bars 4 раза в секунду.
public sealed class FullscreenWatch
{
    /// Обычное окно самой оболочки (настройки, канбан, «Монитор ПК», редактор снимка): впереди
    /// оно — как любое обычное окно, монитор уже не «весь экран». Меню, Пуск и остров — нет.
    public Func<IntPtr, bool> OwnWindow { get; set; } = _ => false;

    readonly FullscreenMap _map = new();
    readonly ExclusiveOwner _exclusive = new();
    IntPtr _lastFg;
    string? _lastWhy;

    /// Мониторы, занятые fullscreen (снимок, можно читать из любого потока).
    public IReadOnlySet<string> Full { get; private set; } = new HashSet<string>();

    /// Монитор игры в эксклюзиве (поверх неё окна не видны) или null.
    public string? Exclusive { get; private set; }

    /// true — набор поменялся.
    public bool Update(IReadOnlyList<Screen> screens)
    {
        SHQueryUserNotificationState(out var state);
        var exclusiveState = state is 3 or 4;
        if (!exclusiveState) _exclusive.Judge(0, false, false, false, false);
        var fg = GetForegroundWindow();
        var own = fg != IntPtr.Zero && ProcessId(fg) == (uint)Environment.ProcessId;
        if (own && OwnWindow(fg) && GetWindowRect(fg, out var or)
            && screens.FirstOrDefault(s => s.Contains((or.Left + or.Right) / 2, (or.Top + or.Bottom) / 2)) is { } ownScreen)
            _map.Observe(ownScreen.Name, (long)fg, false);
        else if (fg != IntPtr.Zero && !own)
        {
            var cls = ClassName(fg);
            if (!ShellUi.Is(cls, ProcNames.Of(fg)) && GetWindowRect(fg, out var r)
                && screens.FirstOrDefault(s => s.Contains((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)) is { } screen)
            {
                var covers = Covers(r, screen.Bounds);
                var resizable = ((long)GetWindowLongPtrW(fg, GWL_STYLE) & 0x00040000 /* WS_THICKFRAME */) != 0;
                var zoomed = IsZoomed(fg);
                var full = _exclusive.Judge((long)fg, exclusiveState, covers, zoomed, resizable);
                _map.Observe(screen.Name, (long)fg, full);
                var why = $"{screen.Name}: {cls} состояние {state} весь экран {covers} развёрнуто {zoomed} рамка {resizable} → {(full ? "полный экран" : "обычное")}";
                if (covers && (fg != _lastFg || why != _lastWhy)) Log.Write($"весь экран: окно {ProcNames.Of(fg)} — {why}");
                _lastFg = fg;
                _lastWhy = why;
            }
        }
        _map.Prune((h, name) =>
        {
            var hw = (IntPtr)h;
            var sc = screens.FirstOrDefault(x => x.Name == name);
            return sc != null && IsWindow(hw) && !IsIconic(hw) && GetWindowRect(hw, out var wr) && Covers(wr, sc.Bounds);
        });
        var set = _map.FullScreens.ToHashSet();
        string? excl = null;
        if (exclusiveState && _exclusive.Owner != 0)
            foreach (var name in set)
                if (_map.Holder(name) == _exclusive.Owner) excl = name;
        var changed = !set.SetEquals(Full) || excl != Exclusive;
        Full = set;
        Exclusive = excl;
        return changed;
    }

    static bool Covers(RECT r, RECT s) => r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom;

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
}
