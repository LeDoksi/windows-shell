namespace WindowsShell.Island.Core;

/// Эксклюзивный режим (SHQueryUserNotificationState = 3 или 4) Windows сообщает на весь сеанс,
/// а не на окно: когда CS2 в эксклюзиве, развёрнутое окно на соседнем мониторе тоже выглядело
/// «полным экраном» (E22, И2). Поэтому эксклюзив принадлежит одному окну — тому, что первым
/// закрыло свой монитор, пока состояние держится. Остальные окна судим обычным правилом.
public sealed class ExclusiveOwner
{
    long _owner;

    public long Owner => _owner;

    public bool Judge(long hwnd, bool exclusiveState, bool covers, bool zoomed, bool resizable)
    {
        var plain = FullscreenRule.IsFull(false, covers, zoomed, resizable);
        if (!exclusiveState)
        {
            _owner = 0;
            return plain;
        }
        if (_owner == 0 && covers) _owner = hwnd;
        return hwnd == _owner || plain;
    }
}

/// Окна самой Windows (Пуск и поиск по клавише Win, переключатель задач, панель задач,
/// рабочий стол): пока они впереди, ничего о fullscreen не решаем — игра остаётся за своим
/// монитором. E22: окно Explorer по Win определялось как «весь экран».
public static class ShellUi
{
    static readonly HashSet<string> Classes = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame", "TaskSwitcherWnd",
        "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow", "Shell_InputSwitchTopLevelWindow",
    };

    static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "ShellHost",
    };

    public static bool Is(string cls, string exe) => Classes.Contains(cls) || Hosts.Contains(exe);
}
