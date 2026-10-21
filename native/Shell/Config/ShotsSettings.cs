using System.Text.Json.Nodes;

namespace WindowsShell.Config;

/// Настройки виджета «Снимки» (design-v2 п. 4). `Enabled` у самого виджета — PrtSc и Shift+PrtSc
/// наши; выключен — клавиши работают как в Windows.
public sealed class ShotsSettings(WidgetEntry entry)
{
    JsonObject S => entry.Settings;

    bool Get(string key, bool fallback) => S[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    /// Значок «Снимки» в хвосте дока (виден всегда, утверждено).
    public bool Icon
    {
        get => Get("icon", true);
        set => S["icon"] = value;
    }

    /// Win+Shift+S тоже открывает наш снимок (иначе «Ножницы» Windows).
    public bool WinShiftS
    {
        get => Get("winShiftS", true);
        set => S["winShiftS"] = value;
    }

    /// Микрофон в записи экрана (запоминается кнопкой на панели записи).
    public bool Mic
    {
        get => Get("mic", false);
        set => S["mic"] = value;
    }

    /// Звук системы в записи экрана.
    public bool SystemSound
    {
        get => Get("systemSound", true);
        set => S["systemSound"] = value;
    }
}
