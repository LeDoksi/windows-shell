using System.Text.Json.Nodes;

namespace WindowsShell.Config;

/// Настройки виджета «Монитор ПК» (design-v2 п. 8): игровой оверлей и отдельное окно включаются
/// независимо, настройки общие, одной страницей.
public sealed class PerfSettings(WidgetEntry entry)
{
    /// Цифры оверлея (порядок по умолчанию). Идут парами: FPS · время кадра, процессор % · °C…
    public static readonly string[] Metrics = ["fps", "frametime", "cpu", "cputemp", "gpu", "gputemp", "ram", "vram"];

    public static string MetricName(string id) => id switch
    {
        "fps" => "FPS",
        "frametime" => "Время кадра",
        "cpu" => "Процессор %",
        "cputemp" => "Процессор °C",
        "gpu" => "Видеокарта %",
        "gputemp" => "Видеокарта °C",
        "ram" => "ОЗУ",
        "vram" => "Видеопамять",
        _ => id,
    };

    public const string TopLeft = "tl", TopRight = "tr", BottomLeft = "bl", BottomRight = "br";
    public static readonly int[] FontSizes = [12, 15, 19];

    JsonObject S => entry.Settings;

    bool Bool(string key, bool fallback) => S[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    int Int(string key, int fallback, int min, int max) => S[key] is JsonValue v && v.TryGetValue<int>(out var n) && n >= min && n <= max ? n : fallback;
    string Str(string key, string fallback, params string[] allowed) => S[key] is JsonValue v && v.TryGetValue<string>(out var s) && allowed.Contains(s) ? s : fallback;

    /// Оверлей поверх игры (по умолчанию включён: показывает только FPS).
    public bool Overlay { get => Bool("overlay", true); set => S["overlay"] = value; }

    /// Окно «Монитор ПК».
    public bool Window { get => Bool("window", false); set => S["window"] = value; }

    /// Окно поверх всех окон.
    public bool WindowTopmost { get => Bool("windowTopmost", true); set => S["windowTopmost"] = value; }

    /// Где стоит окно: физические пиксели экрана (null — ещё не ставили).
    public (int X, int Y, int W, int H)? WindowRect
    {
        get => S["windowRect"] is JsonArray a && a.Count == 4 && a.All(n => n is JsonValue v && v.TryGetValue<int>(out _))
            ? (a[0]!.GetValue<int>(), a[1]!.GetValue<int>(), a[2]!.GetValue<int>(), a[3]!.GetValue<int>()) : null;
        set => S["windowRect"] = value is { } r ? new JsonArray(r.X, r.Y, r.W, r.H) : null;
    }

    /// Все цифры оверлея в выбранном порядке.
    public List<string> Order
    {
        get
        {
            var list = S["order"] is JsonArray a ? a.Select(n => n?.GetValue<string>()).OfType<string>().Where(Metrics.Contains).Distinct().ToList() : [];
            list.AddRange(Metrics.Where(m => !list.Contains(m)));
            return list;
        }
        set => S["order"] = new JsonArray(value.Select(v => (JsonNode)v).ToArray());
    }

    /// Какие цифры показывать (по умолчанию только FPS).
    public HashSet<string> Shown
    {
        get => S["shown"] is JsonArray a ? a.Select(n => n?.GetValue<string>()).OfType<string>().Where(Metrics.Contains).ToHashSet() : ["fps"];
        set => S["shown"] = new JsonArray(Metrics.Where(value.Contains).Select(v => (JsonNode)v).ToArray());
    }

    /// Показанные цифры по порядку.
    public List<string> Items
    {
        get
        {
            var shown = Shown;
            return Order.Where(shown.Contains).ToList();
        }
    }

    /// Одна строка или столбик.
    public bool Column { get => Str("layout", "row", "row", "column") == "column"; set => S["layout"] = value ? "column" : "row"; }

    public string Corner { get => Str("corner", TopLeft, TopLeft, TopRight, BottomLeft, BottomRight); set => S["corner"] = value; }

    /// Размер шрифта, точки при 100 %.
    public int FontSize { get => Int("fontSize", FontSizes[1], 9, 40); set => S["fontSize"] = value; }

    /// Непрозрачность фона, %.
    public int Background { get => Int("background", 55, 0, 100); set => S["background"] = value; }

    /// FPS ниже — жёлтый (0 — не подсвечивать).
    public int FpsWarn { get => Int("fpsWarn", 30, 0, 1000); set => S["fpsWarn"] = value; }

    /// Вкл/выкл оверлей, пока в фокусе игра (клавиша действует только в игре).
    public string? Hotkey
    {
        get => S["hotkey"] is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Length == 0 ? null : s) : "Ctrl+Shift+F";
        set => S["hotkey"] = value ?? "";
    }

    /// Ручные отметки: путь к exe → true «это игра», false «не игра».
    public Dictionary<string, bool> Marks
    {
        get
        {
            var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (S["games"] is JsonObject o)
                foreach (var (k, v) in o)
                    if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) d[k] = b;
            return d;
        }
    }

    public void Mark(string exe, bool? game)
    {
        if (S["games"] is not JsonObject o) S["games"] = o = new JsonObject();
        var key = o.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, exe, StringComparison.OrdinalIgnoreCase)) ?? exe;
        if (game is { } g) o[key] = g;
        else o.Remove(key);
    }
}
