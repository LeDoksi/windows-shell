using System.Text.Json.Nodes;

namespace WindowsShell.Config;

/// Профиль звука: «Наушники 40 %» — устройство и громкость одним кликом или хоткеем.
public sealed record AudioProfile(string Id, string Name, string DeviceId, int Volume, string? Hotkey);

/// Настройки виджета «Звук» (тот же формат, что `src/widgets/audio/settings.ts`).
public sealed class AudioSettings(WidgetEntry entry)
{
    JsonObject S => entry.Settings;

    /// Шаг колеса мыши над значком, %.
    public int Step
    {
        get => S["step"] is JsonValue v && v.TryGetValue<int>(out var n) ? Math.Clamp(n, 1, 20) : 2;
        set => S["step"] = Math.Clamp(value, 1, 20);
    }

    public bool ShowApps
    {
        get => S["showApps"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
        set => S["showApps"] = value;
    }

    public bool ShowMic
    {
        get => S["showMic"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
        set => S["showMic"] = value;
    }

    public string? NextOutputHotkey
    {
        get => (string?)S["nextOutputHotkey"];
        set => S["nextOutputHotkey"] = value;
    }

    public List<AudioProfile> Profiles
    {
        get
        {
            var list = new List<AudioProfile>();
            foreach (var p in S["profiles"]?.AsArray() ?? [])
            {
                if ((string?)p?["id"] is not { } id || (string?)p?["deviceId"] is not { } device) continue;
                var volume = p?["volume"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 50;
                list.Add(new AudioProfile(id, (string?)p?["name"] ?? id, device, volume, (string?)p?["hotkey"]));
            }
            return list;
        }
        set => S["profiles"] = new JsonArray(value.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id,
            ["name"] = p.Name,
            ["deviceId"] = p.DeviceId,
            ["volume"] = p.Volume,
            ["hotkey"] = p.Hotkey,
        }).ToArray());
    }

    /// «Наушники (Sony WH-1000XM5)» → «Наушники»; если короткое имя у нескольких устройств — полное.
    public static string ShortName(string name, IEnumerable<string> all)
    {
        static string Short(string n)
        {
            var i = n.IndexOf(" (", StringComparison.Ordinal);
            var s = (i > 0 && n.EndsWith(')') ? n[..i] : n).Trim();
            return s.Length > 0 ? s : n;
        }
        var shortName = Short(name);
        return all.Count(n => Short(n) == shortName) > 1 ? name : shortName;
    }
}
