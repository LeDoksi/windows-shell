namespace WindowsShell.Island.Core;

public enum EventKind { Language, Charge, Shot, Notice }

/// На каком экране показать событие (раздел 2, В2, П6). Вне игры — на экране активного окна.
/// Раскладка в fullscreen не показывается вовсе и на другой экран не уходит. Низкий заряд —
/// исключение: показывается и поверх игры; но поверх эксклюзива ничего не видно, поэтому
/// тогда — на свободном соседнем экране (если его нет — там же: остаётся звук в наушники).
public static class EventRoute
{
    public static string? Target(EventKind kind, string active, ISet<string> full, string? exclusive, IReadOnlyList<string> screens)
    {
        switch (kind)
        {
            case EventKind.Language:
                return full.Contains(active) ? null : active;
            case EventKind.Shot:
            case EventKind.Notice:
                // Результат снимка и уведомление: в игре — на свободный другой экран, иначе тихо (он в галерее).
                if (!full.Contains(active)) return active;
                return screens.FirstOrDefault(s => s != active && !full.Contains(s));
            default:
                if (exclusive != active) return active;
                return screens.FirstOrDefault(s => s != active && !full.Contains(s))
                    ?? screens.FirstOrDefault(s => s != active)
                    ?? active;
        }
    }
}
