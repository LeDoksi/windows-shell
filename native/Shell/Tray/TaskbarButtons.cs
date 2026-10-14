using Microsoft.UI.Xaml.Media;

namespace WindowsShell.Tray;

/// Счётчики и полоски загрузки на иконках дока (раздел 8, WSH-83). Программы задают их через
/// ITaskbarList (SetOverlayIcon, SetProgressState/Value); Windows пересылает это нашему трею,
/// а здесь хранится последнее по каждому окну.
public sealed class TaskbarButtons
{
    /// Состояния TBPFLAG: 0 — нет, 1 — бегущая полоска, 2 — обычная, 4 — ошибка, 8 — пауза.
    public const int None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8;

    public sealed record ProgressInfo(int State, double Value);

    readonly Dictionary<IntPtr, ProgressInfo> _progress = [];
    readonly Dictionary<IntPtr, ImageSource> _overlays = [];
    bool _logged;

    public event Action? Changed;

    public TaskbarButtons(TrayHost tray)
    {
        tray.Progress += OnProgress;
        tray.Overlay += OnOverlay;
    }

    void OnProgress(IntPtr hwnd, int state, double value)
    {
        var old = _progress.GetValueOrDefault(hwnd) ?? new ProgressInfo(None, 0);
        // SetProgressValue сам включает обычную полоску, если её не было.
        var next = state >= 0 ? old with { State = state } : old with { Value = value, State = old.State == None ? Normal : old.State };
        if (!_logged && value >= 0) { _logged = true; Log.Write($"полоска загрузки: первое значение {value:0.###} (окно 0x{hwnd:X})"); }
        if (next.State == None) _progress.Remove(hwnd);
        else _progress[hwnd] = next;
        if (next != old) Changed?.Invoke();
    }

    void OnOverlay(IntPtr hwnd, TrayPixels? px)
    {
        if (px is null) { if (_overlays.Remove(hwnd)) Changed?.Invoke(); return; }
        _overlays[hwnd] = TrayFlyout.ToBitmap(px);
        Changed?.Invoke();
    }

    /// Полоска программы: по первому окну, у которого она есть.
    public ProgressInfo? ProgressOf(IEnumerable<IntPtr> windows)
    {
        foreach (var w in windows)
            if (_progress.TryGetValue(w, out var p)) return p;
        return null;
    }

    public ImageSource? OverlayOf(IEnumerable<IntPtr> windows)
    {
        foreach (var w in windows)
            if (_overlays.TryGetValue(w, out var o)) return o;
        return null;
    }

    /// Окно закрылось — забываем его счётчик и полоску.
    public void Prune(IReadOnlySet<IntPtr> alive)
    {
        var gone = _progress.Keys.Concat(_overlays.Keys).Where(h => !alive.Contains(h)).ToList();
        if (gone.Count == 0) return;
        foreach (var h in gone) { _progress.Remove(h); _overlays.Remove(h); }
        Changed?.Invoke();
    }
}
