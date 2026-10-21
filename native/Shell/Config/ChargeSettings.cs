using System.Text.Json.Nodes;

namespace WindowsShell.Config;

/// Настройки виджета «Заряд устройств» (design-v2 п. 7). Когда виден значок — общая настройка
/// временных значков (`ShellConfig.TempWidgetMode("charge")`), «Никогда» = виджет выключен:
/// ни значка, ни уведомлений.
public sealed class ChargeSettings(WidgetEntry entry)
{
    public static readonly int[] Thresholds = [10, 15, 20, 25, 30, 40];

    JsonObject S => entry.Settings;

    /// Порог «пора зарядить», %. Второе уведомление всегда при 10 %.
    public int Threshold
    {
        get => S["threshold"] is JsonValue v && v.TryGetValue<int>(out var n) && n is >= 5 and <= 50 ? n : 20;
        set => S["threshold"] = value;
    }

    /// Мягкий сигнал в наушники, когда идёт полноэкранная игра (Windows прячет уведомления).
    public bool GameSound
    {
        get => S["gameSound"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : true;
        set => S["gameSound"] = value;
    }

    /// Устройства, за которыми не следим: ключ → имя (имя — чтобы показать в настройках).
    public IReadOnlyDictionary<string, string> Ignored =>
        S["ignored"] is JsonObject o ? o.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? p.Key) : new Dictionary<string, string>();

    public void Ignore(string key, string name)
    {
        if (S["ignored"] is not JsonObject o) S["ignored"] = o = new JsonObject();
        o[key] = name;
    }

    public void Unignore(string key)
    {
        if (S["ignored"] is JsonObject o) o.Remove(key);
    }
}
