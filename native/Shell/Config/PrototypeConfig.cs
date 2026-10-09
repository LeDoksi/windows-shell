using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsShell.Config;

public sealed record Pin(string Id, string Title, string Target);

/// Настройки прототипа. Первый запуск берёт закреплённое и категории из конфига версии на Tauri
/// (`%APPDATA%\com.ledoksi.windowsshell\config.json`) только для чтения; дальше прототип пишет
/// в свой файл `%APPDATA%\WindowsShellNative\prototype.json` и чужой конфиг не трогает.
public sealed class PrototypeConfig
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsShellNative");
    static readonly string OwnFile = Path.Combine(Dir, "prototype.json");
    static readonly string TauriFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.ledoksi.windowsshell", "config.json");

    public List<Pin> Pins { get; private set; } = [];

    /// Категория → цели, которые прототип в неё добавил (меню «Добавить в категорию»).
    public Dictionary<string, List<string>> Categories { get; private set; } = [];

    public static PrototypeConfig Load()
    {
        var c = new PrototypeConfig();
        try
        {
            if (File.Exists(OwnFile)) c.Read(JsonNode.Parse(File.ReadAllText(OwnFile)), own: true);
            else if (File.Exists(TauriFile)) c.Read(JsonNode.Parse(File.ReadAllText(TauriFile)), own: false);
        }
        catch
        {
            // Битый файл — начинаем с пустого дока, файл Tauri не трогаем.
        }
        if (c.Pins.Count == 0)
            c.Pins.Add(new Pin("explorer", "Проводник", "Microsoft.Windows.Explorer"));
        if (c.Categories.Count == 0)
            foreach (var name in new[] { "Работа", "Игры", "Личное" }) c.Categories[name] = [];
        return c;
    }

    void Read(JsonNode? root, bool own)
    {
        if (root is null) return;
        var pins = own ? root["pins"] : root["dock"]?["pinned"];
        foreach (var p in pins?.AsArray() ?? [])
        {
            var id = (string?)p?["id"];
            var title = (string?)p?["title"];
            var target = (string?)p?["target"];
            if (id != null && title != null && target != null) Pins.Add(new Pin(id, title, target));
        }
        if (own)
        {
            foreach (var (name, items) in root["categories"]?.AsObject() ?? [])
                Categories[name] = items?.AsArray().Select(i => (string?)i ?? "").Where(s => s != "").ToList() ?? [];
        }
        else
        {
            foreach (var cat in root["categories"]?.AsArray() ?? [])
                if ((string?)cat?["name"] is { } name) Categories[name] = [];
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var root = new JsonObject
            {
                ["pins"] = new JsonArray(Pins.Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id, ["title"] = p.Title, ["target"] = p.Target,
                }).ToArray()),
                ["categories"] = new JsonObject(Categories.Select(kv =>
                    new KeyValuePair<string, JsonNode?>(kv.Key, new JsonArray(kv.Value.Select(v => (JsonNode)v).ToArray())))),
            };
            var tmp = OwnFile + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, OwnFile, overwrite: true);
        }
        catch
        {
            // Прототип: не сохранилось — не страшно.
        }
    }
}
