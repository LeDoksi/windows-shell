namespace WindowsShell.Island.Core;

/// Где строка закреплённого «Монитора ПК» (П8). Закреплена за экраном по имени; экрана сейчас
/// нет — закрепление «спит» и нигде не появляется, вернули экран — строка снова там.
public static class PinPlace
{
    public static (string? Screen, bool Sleeping) Where(string? pinned, IReadOnlyList<string> screens)
    {
        if (string.IsNullOrEmpty(pinned)) return (null, false);
        return screens.Contains(pinned) ? (pinned, false) : (null, true);
    }
}
