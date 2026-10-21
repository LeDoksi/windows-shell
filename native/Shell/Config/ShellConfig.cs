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
    /// Блок «Недавние файлы» (выключается в окне настроек).
    public bool RecentFiles { get; set; } = true;
    /// Быстрые папки в нижней строке: explorer, documents, downloads, pictures, music, videos, settings.
    public List<string> Folders { get; set; } = ["explorer", "downloads", "settings"];
    /// Сколько раз что запускали из Пуска: частое выше в поиске.
    public Dictionary<string, int> Launches { get; set; } = [];
}

/// Наши сочетания с Win (окно настроек → «Клавиши»). Выключил — клавиша работает как в Windows.
public sealed class KeysSection
{
    /// Win одна открывает наш Пуск (иначе родной).
    public bool WinStart { get; set; } = true;
    /// Win+X — наше служебное меню.
    public bool WinX { get; set; } = true;
    /// Win+S и Win+Q — поиск в нашем Пуске.
    public bool WinSearch { get; set; } = true;
    /// Win+1…9, Win+T, Win+B — наш док.
    public bool DockKeys { get; set; } = true;
}

/// Системный хвост и панель задач Windows (WSH-E9). Часы видны всегда.
public sealed class TailSection
{
    /// Прятать панель задач Windows (её работу делают док и хвост).
    public bool HideTaskbar { get; set; } = true;
    /// ⌃ Фоновые приложения (свой трей).
    public bool Tray { get; set; } = true;
    public bool Network { get; set; } = true;
    /// Только на ноутбуке: на ПК без батареи значка нет в любом случае.
    public bool Battery { get; set; } = true;
    public bool Language { get; set; } = true;
    public bool Notifications { get; set; } = true;
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
    public KeysSection Keys { get; set; } = new();
    public TailSection Tail { get; set; } = new();
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
    public KeysSection Keys => Data.Keys;
    public TailSection Tail => Data.Tail;

    enum Read { Ok, Missing, Broken, Unavailable }

    public static ShellConfig Load()
    {
        var c = new ShellConfig();
        var (own, ownState) = c.TryRead(FilePath);
        if (own != null) { c.Data = own; c.Normalize(); KeepDailyCopy(); return c; }
        if (ownState == Read.Unavailable)
        {
            // Папку оболочки не дают читать (так было при входе в Windows 15.10: File.Exists
            // ответил «нет», и оболочка показала старую раскладку Tauri как при первом запуске,
            // WSH-99). Работаем на пустой раскладке, ничего не пишем и ждём, пока файл откроется.
            Log.Write("конфиг недоступен: жду, ничего не записываю");
            c.Data = Defaults();
            c.Normalize();
            c.WaitForFile();
            return c;
        }
        var (bak, bakState) = c.TryRead(FilePath + ".bak");
        if (ownState == Read.Broken || bakState != Read.Missing || c.NewestDaily() != null)
        {
            // Конфиг был, но не прочитался: это не первый запуск, конфиг Tauri сюда не берём.
            // Испорченный файл откладываем с датой, чтобы ничего не потерять.
            if (ownState == Read.Broken)
                try { File.Copy(FilePath, Path.Combine(Dir, $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true); } catch { }
            var restored = bak ?? c.NewestDaily();
            if (restored != null)
            {
                Log.Write(bak != null ? "конфиг не прочитан, беру бэкап" : "конфиг и бэкап не прочитаны, беру дневную копию");
                c.Data = restored;
                c.Normalize();
                c.Save();
                return c;
            }
            Log.Write("конфиг и все копии не читаются, начинаю с пустой раскладки");
            c.Data = Defaults();
            c.Normalize();
            if (bakState == Read.Unavailable) c.WaitForFile(); else c.Save();
            return c;
        }
        // Первый запуск: переезд с версии на Tauri.
        var (tauri, _) = c.TryRead(Path.Combine(TauriDir, "config.json"));
        c.Data = tauri ?? Defaults();
        c.ImportPrototypePins();
        if (tauri is null && !File.Exists(PrototypeFile)) c.ImportTaskbarPins();
        c.Normalize();
        c.Save();
        Log.Write($"конфиг создан: {c.Pins.Count} в доке, {c.Categories.Count} категорий");
        return c;
    }

    // Запущены без конфига (файл не открывался): не пишем, пока он не прочитается.
    volatile bool _waiting;
    Timer? _retry;

    /// Каждые 2 секунды пробуем прочитать конфиг; вышло — подменяем раскладку и сообщаем всем.
    void WaitForFile()
    {
        _waiting = true;
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _retry = new Timer(_ =>
        {
            var (data, state) = TryRead(FilePath, attempts: 1, quiet: true);
            if (data is null && state == Read.Unavailable) return;
            _retry?.Dispose();
            void Apply()
            {
                if (data != null) { Data = data; Normalize(); KeepDailyCopy(); Log.Write("конфиг снова доступен, раскладка загружена"); }
                else Log.Write($"конфиг снова доступен, но не прочитан ({state})");
                _waiting = false;
                Changed?.Invoke();
            }
            if (ui is null || !ui.TryEnqueue(Apply)) Apply();
        }, null, 2000, 2000);
    }

    static readonly string DailyDir = Path.Combine(Dir, "backups");

    /// Раз в день — копия конфига в `backups\config-ГГГГММДД.json`, последние 14.
    static void KeepDailyCopy()
    {
        try
        {
            Directory.CreateDirectory(DailyDir);
            var today = Path.Combine(DailyDir, $"config-{DateTime.Now:yyyyMMdd}.json");
            if (!File.Exists(today)) File.Copy(FilePath, today);
            foreach (var old in Directory.GetFiles(DailyDir, "config-*.json").OrderDescending().Skip(14)) File.Delete(old);
        }
        catch (Exception e) { Log.Write($"дневная копия конфига не сделана: {e.Message}"); }
    }

    ConfigData? NewestDaily()
    {
        try
        {
            if (!Directory.Exists(DailyDir)) return null;
            foreach (var f in Directory.GetFiles(DailyDir, "config-*.json").OrderDescending())
                if (TryRead(f, attempts: 1).Data is { } d) return d;
        }
        catch { }
        return null;
    }

    /// Прочитать конфиг. File.Exists отвечает «нет» и когда файл есть, но его не дают открыть,
    /// поэтому читаем сразу и различаем: нет файла, испорчен, недоступен (занят или нет доступа —
    /// тогда пробуем ещё несколько секунд).
    (ConfigData? Data, Read State) TryRead(string path, int attempts = 20, bool quiet = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var data = JsonSerializer.Deserialize<ConfigData>(File.ReadAllText(path), Json);
                return data is { SchemaVersion: 1 } ? (data, Read.Ok) : (null, Read.Broken);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return (null, Read.Missing);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts) { if (!quiet) Log.Write($"конфиг недоступен ({path}): {e.GetType().Name} {e.Message}"); return (null, Read.Unavailable); }
                Thread.Sleep(250);
            }
            catch (Exception e)
            {
                Log.Write($"конфиг не читается ({path}): {e.Message}");
                return (null, Read.Broken);
            }
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

    /// Совсем первый запуск (раздел 8): в док — то, что закреплено в панели задач Windows, в
    /// том же порядке. Ярлыки закреплённого лежат в «User Pinned\TaskBar», а порядок Windows
    /// хранит в Taskband\Favorites, где встречаются имена этих ярлыков.
    void ImportTaskbarPins()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (!Directory.Exists(dir)) return;
            var order = "";
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband"))
                if (key?.GetValue("Favorites") is byte[] blob) order = System.Text.Encoding.Unicode.GetString(blob);
            var links = Directory.GetFiles(dir, "*.lnk")
                .Select(f => (Path: f, At: order.IndexOf(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.At < 0 ? int.MaxValue : x.At)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Path);
            var pins = Data.Dock.Pinned;
            foreach (var link in links)
            {
                var title = Path.GetFileNameWithoutExtension(link);
                // Проводник в доке уже есть.
                if (title.Contains("Explorer", StringComparison.OrdinalIgnoreCase) || title.Contains("Проводник", StringComparison.OrdinalIgnoreCase)) continue;
                pins.Add(new Pin(NewId(title), title, KeepShortcut(link)));
            }
            Log.Write($"первый запуск: из панели задач взято {pins.Count - 1}");
        }
        catch (Exception e) { Log.Write($"первый запуск: закреплённое из панели задач не прочитано ({e.Message})"); }
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
        Data.Keys ??= new();
        Data.Tail ??= new();
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
        // Запущены без конфига: не затираем раскладку на диске, пока он не прочитается.
        if (_waiting) { Log.Write("конфиг не записан: он ещё недоступен"); return; }
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

    /// Временный значок виджета в хвосте (design-v2 п. 2): «Когда нужно» (по умолчанию),
    /// «Всегда» или «Никогда» (= виджет выключен).
    public const string ShowWhenNeeded = "auto", ShowAlways = "always", ShowNever = "never";

    public string TempWidgetMode(string id)
    {
        var w = Data.Widgets.FirstOrDefault(x => x.Id == id);
        if (w is { Enabled: false }) return ShowNever;
        return w?.Settings["show"]?.GetValue<string>() == ShowAlways ? ShowAlways : ShowWhenNeeded;
    }

    public void SetTempWidgetMode(string id, string mode)
    {
        var w = Widget(id);
        w.Enabled = mode != ShowNever;
        w.Settings["show"] = mode == ShowAlways ? ShowAlways : ShowWhenNeeded;
    }

    /// Виден ли временный значок: `needed` — виджету сейчас есть что показать.
    public bool TempWidgetVisible(string id, bool needed) => TempWidgetMode(id) switch
    {
        ShowAlways => true,
        ShowNever => false,
        _ => needed,
    };

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
            // Тот же ярлык уже лежит у нас (бросили второй раз, два быстрых броска): берём его,
            // иначе копия «(2)» получила бы другой путь и плитка задвоилась бы (WSH-59).
            var bytes = File.ReadAllBytes(path);
            for (var n = 2; File.Exists(dest); n++)
            {
                if (File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes)) return dest;
                dest = Path.Combine(dir, $"{name} ({n}){ext}");
            }
            File.WriteAllBytes(dest, bytes);
            return dest;
        }
        catch { return path; }
    }
}
