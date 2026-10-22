namespace WindowsShell.Apps;

/// Обычные окна самой оболочки (настройки, канбан, «Монитор ПК», редактор снимка). Док
/// показывает их как отдельные программы: своё имя и значок из шрифта вместо иконки exe.
/// Окно, которое тут не записано, тоже попадёт в док (правило общее: всё, что было бы на
/// панели задач), просто с иконкой оболочки и своим заголовком.
public static class OwnWindows
{
    public sealed record Info(string Key, string Name, string Glyph);

    static readonly Dictionary<IntPtr, Info> Known = [];

    public static void Register(IntPtr hwnd, string key, string name, string glyph) => Known[hwnd] = new Info(key, name, glyph);

    public static Info? Of(IntPtr hwnd) => Known.GetValueOrDefault(hwnd);

    /// Закрытые окна забываем (их номер может достаться новому окну).
    public static void Prune()
    {
        foreach (var h in Known.Keys.Where(h => !Native.Win32.IsWindow(h)).ToList()) Known.Remove(h);
    }
}
