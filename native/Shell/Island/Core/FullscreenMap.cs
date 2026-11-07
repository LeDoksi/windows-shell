namespace WindowsShell.Island.Core;

/// Правило «окно на весь экран» — ровно то же, что в Ui/Bars.cs (FullscreenScreens):
/// эксклюзивный Direct3D/презентация, или окно закрывает монитор и при этом не обычное
/// развёрнутое окно с рамкой (WSH-100: ChatGPT и Electron без заголовка — не fullscreen).
public static class FullscreenRule
{
    public static bool IsFull(bool exclusiveState, bool coversScreen, bool zoomed, bool resizable) =>
        exclusiveState || (coversScreen && (!zoomed || !resizable));
}

/// Fullscreen по каждому монитору отдельно. Bars помнит только окно на переднем плане,
/// поэтому клик на второй монитор «снимал» игру с первого. Здесь полное окно запоминается
/// за своим монитором, пока его не закроют, не свернут или на том же мониторе не выйдет
/// вперёд обычное окно.
public sealed class FullscreenMap
{
    readonly Dictionary<string, long> _full = new(StringComparer.Ordinal);

    /// Окно на переднем плане (не окно оболочки) на мониторе screen.
    public void Observe(string screen, long hwnd, bool full)
    {
        if (full)
        {
            // Одно окно — один монитор: если игру перетащили, со старого монитора она уходит.
            foreach (var other in _full.Where(p => p.Value == hwnd && p.Key != screen).Select(p => p.Key).ToList())
                _full.Remove(other);
            _full[screen] = hwnd;
        }
        else
        {
            _full.Remove(screen);
        }
    }

    /// stillFull(hwnd, screen) — окно живо, не свёрнуто и всё ещё закрывает этот монитор.
    public void Prune(Func<long, string, bool> stillFull)
    {
        foreach (var (screen, hwnd) in _full.ToList())
            if (!stillFull(hwnd, screen)) _full.Remove(screen);
    }

    public bool IsFull(string screen) => _full.ContainsKey(screen);

    public IReadOnlyCollection<string> FullScreens => _full.Keys.ToList();
}
