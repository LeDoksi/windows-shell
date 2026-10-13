using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WindowsShell.Config;

// Схема конфига та же, что у версии на Tauri (`src-tauri/src/config.rs`, schemaVersion 1):
// так её файл переезжает целиком, без потерь (док, категории, виджеты, профили звука).

public sealed class LauncherItem
{
    public string Id { get; set; } = "";
    /// app · file · folder · url
    public string Kind { get; set; } = "app";
    public string Title { get; set; } = "";
    public string Target { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Meta { get; set; }
}

public sealed class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#43B4F5";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFolder { get; set; }
    public List<LauncherItem> Items { get; set; } = [];
}

public sealed record Pin(string Id, string Title, string Target);

public sealed class DockSection
{
    public List<Pin> Pinned { get; set; } = [];
}

public sealed class Appearance
{
    /// acrylic — «Стекло» (размытие), tint — «Тонировка» (ровная тёмная заливка).
    public string Glass { get; set; } = "acrylic";
}

public sealed class Bars
{
    public bool LauncherPinned { get; set; }
    public bool DockPinned { get; set; }
    /// Монитор системного хвоста: имя устройства (`\\.\DISPLAY2`); null или не подключён — основной.
    public string? Monitor { get; set; }
}

/// Свой Пуск (WSH-E15).
public sealed class StartSection
{
    /// Последняя открытая вкладка: id категории или «all».
    public string? LastTab { get; set; }
    /// Блок «Недавние файлы» (выключается в меню трея, потом в окне настроек).
    public bool RecentFiles { get; set; } = true;
    /// Быстрые папки в нижней строке: explorer, documents, downloads, pictures, music, videos, settings.
    public List<string> Folders { get; set; } = ["explorer", "downloads", "settings"];
    /// Сколько раз что запускали из Пуска: частое выше в поиске.
    public Dictionary<string, int> Launches { get; set; } = [];
}

public sealed class WidgetEntry
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public JsonObject Settings { get; set; } = new();
}

public sealed class ConfigData
{
    public int SchemaVersion { get; set; } = 1;
    public List<Category> Categories { get; set; } = [];
    public DockSection Dock { get; set; } = new();
    public Appearance Appearance { get; set; } = new();
    public Bars Bars { get; set; } = new();
    public List<WidgetEntry> Widgets { get; set; } = [];
    public StartSection Start { get; set; } = new();
}

/// Конфиг оболочки на диске: `%APPDATA%\WindowsShellNative\config.json`. Запись атомарная,
/// предыдущая версия лежит рядом в `config.json.bak`, испорченный файл откладывается в
/// `config.broken.json`. Первый запуск берёт всё из конфига версии на Tauri (его не трогаем),
/// а закреплённое в доке — из файла прототипа, если он есть (там свежее).
public sealed class ShellConfig
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsShellNative");
    static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly string TauriDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.ledoksi.windowsshell");
    static readonly string PrototypeFile = Path.Combine(Dir, "prototype.json");

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly string[] Colors = ["#43B4F5", "#B07CF0", "#56D27A", "#F0A64A", "#F06B8E", "#4CC3D9", "#F2C94C", "#8E9AAF"];

    public ConfigData Data { get; private set; } = new();

    /// Что-то поменялось и уже записано на диск.
    public event Action? Changed;

    public List<Pin> Pins => Data.Dock.Pinned;
    public List<Category> Categories => Data.Categories;
    public Bars Bars => Data.Bars;
    public bool Tint => Data.Appearance.Glass == "tint";
    public StartSection Start => Data.Start;

    public static ShellConfig Load()
    {
        var c = new ShellConfig();
        if (c.TryRead(FilePath) is { } own) { c.Data = own; c.Normalize(); return c; }
        if (File.Exists(FilePath))
        {
            Log.Write("конфиг испорчен, беру бэкап");
            try { File.Move(FilePath, Path.Combine(Dir, "config.broken.json"), overwrite: true); } catch { }
            if (c.TryRead(FilePath + ".bak") is { } bak) { c.Data = bak; c.Normalize(); c.Save(); return c; }
        }
        // Первый запуск: переезд с версии на Tauri.
        c.Data = c.TryRead(Path.Combine(TauriDir, "config.json")) ?? Defaults();
        c.ImportPrototypePins();
        c.Normalize();
        c.Save();
        Log.Write($"конфиг создан: {c.Pins.Count} в доке, {c.Categories.Count} категорий");
        return c;
    }

    ConfigData? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(path), Json);
            return data is { SchemaVersion: 1 } ? data : null;
        }
        catch (Exception e)
        {
            Log.Write($"конфиг не читается ({path}): {e.Message}");
            return null;
        }
    }

    /// Прототип (WSH-E13) хранил закреплённое в своём файле, и Георгий мог его поменять.
    void ImportPrototypePins()
    {
        try
        {
            if (!File.Exists(PrototypeFile)) return;
            var root = JsonNode.Parse(File.ReadAllText(PrototypeFile));
            var pins = new List<Pin>();
            foreach (var p in root?["pins"]?.AsArray() ?? [])
            {
                if ((string?)p?["id"] is { } id && (string?)p?["title"] is { } title && (string?)p?["target"] is { } target)
                    pins.Add(new Pin(id, title, target));
            }
            if (pins.Count > 0) Data.Dock.Pinned = pins;
        }
        catch { }
    }

    static ConfigData Defaults() => new()
    {
        Categories =
        [
            new() { Id = "work", Name = "Работа", Color = Colors[0] },
            new() { Id = "games", Name = "Игры", Color = Colors[3] },
            new() { Id = "personal", Name = "Личное", Color = Colors[2] },
        ],
        Dock = new() { Pinned = [new Pin("explorer", "Проводник", "Microsoft.Windows.Explorer")] },
    };

    /// Повторяющиеся id чинятся, недостающие виджеты добавляются.
    void Normalize()
    {
        Data.Categories ??= [];
        Data.Dock ??= new();
        Data.Dock.Pinned ??= [];
        Data.Appearance ??= new();
        Data.Bars ??= new();
        Data.Widgets ??= [];
        Data.Start ??= new();
        Data.Start.Folders ??= ["explorer", "downloads", "settings"];
        Data.Start.Launches ??= [];
        var seen = new HashSet<string>();
        Data.Dock.Pinned = Data.Dock.Pinned.Where(p => p != null && seen.Add("pin:" + p.Id)).ToList();
        foreach (var c in Data.Categories)
        {
            c.Items ??= [];
            if (!seen.Add("cat:" + c.Id)) c.Id = NewId("category");
            foreach (var i in c.Items) if (!seen.Add("item:" + i.Id)) i.Id = NewId(i.Title);
        }
        foreach (var id in new[] { "audio", "media" })
            if (Data.Widgets.All(w => w.Id != id)) Data.Widgets.Add(new WidgetEntry { Id = id });
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            using (var f = File.Create(tmp))
            {
                JsonSerializer.Serialize(f, Data, Json);
                f.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Error("запись конфига", e);
        }
    }

    /// Изменить, записать и сообщить всем.
    public void Update(Action<ConfigData> change)
    {
        change(Data);
        Save();
        Changed?.Invoke();
    }

    // ---------- помощники ----------

    public string NewId(string title)
    {
        var slug = new string(title.ToLowerInvariant().Select(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "item";
        if (slug.Length > 24) slug = slug[..24];
        var taken = new HashSet<string>(Data.Categories.Select(c => c.Id)
            .Concat(Data.Categories.SelectMany(c => c.Items.Select(i => i.Id)))
            .Concat(Data.Dock.Pinned.Select(p => p.Id)));
        var id = slug;
        for (var n = 2; taken.Contains(id); n++) id = $"{slug}-{n}";
        return id;
    }

    public Category? CategoryById(string id) => Data.Categories.FirstOrDefault(c => c.Id == id);

    public WidgetEntry Widget(string id)
    {
        var w = Data.Widgets.FirstOrDefault(x => x.Id == id);
        if (w is null) { w = new WidgetEntry { Id = id }; Data.Widgets.Add(w); }
        return w;
    }

    public bool WidgetEnabled(string id) => Data.Widgets.FirstOrDefault(x => x.Id == id)?.Enabled ?? true;

    static readonly HashSet<string> Programs = ["exe", "lnk", "url", "appref-ms", "bat", "cmd"];

    public static bool IsProgram(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return Programs.Contains(ext) && File.Exists(path);
    }

    /// Плитка для файла или папки (как `itemFromPath` во фронте Tauri).
    public static LauncherItem ItemFromPath(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = path;
        if (Directory.Exists(path)) return new LauncherItem { Kind = "folder", Title = name, Target = path, Meta = "Папка" };
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (Programs.Contains(ext))
            return new LauncherItem { Kind = "app", Title = Path.GetFileNameWithoutExtension(name), Target = path, Meta = "Приложение" };
        return new LauncherItem { Kind = "file", Title = name, Target = path, Meta = ext.Length > 0 ? $"Файл {ext.ToUpperInvariant()}" : "Файл" };
    }

    /// Ярлыки с рабочего стола копируем к себе: оригинал можно удалить, плитка останется.
    public static string KeepShortcut(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".lnk" or ".url")) return path;
        try
        {
            var dir = Path.Combine(Dir, "shortcuts");
            if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return path;
            Directory.CreateDirectory(dir);
            var name = Path.GetFileNameWithoutExtension(path);
            var dest = Path.Combine(dir, name + ext);
            for (var n = 2; File.Exists(dest); n++) dest = Path.Combine(dir, $"{name} ({n}){ext}");
            File.Copy(path, dest);
            return dest;
        }
        catch { return path; }
    }
}
