using System.Text.Json.Nodes;

namespace WindowsShell.Config;

/// Настройки виджета «Канбан» (design-v2 п. 9): значок в хвосте и отдельное окно.
public sealed class KanbanSettings(WidgetEntry entry)
{
    /// Колонки окна по порядку (утверждено: В работе · Ждёт · Бэклог · Пауза; «Готово» свёрнуто внизу).
    public static readonly string[] Columns = ["doing", "waiting", "backlog", "hold"];

    public const string SurfaceShell = "shell", SurfaceGlass = "glass", SurfaceTint = "tint";

    JsonObject S => entry.Settings;

    bool Bool(string key, bool fallback) => S[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
    string? Str(string key) => S[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// Окно канбана открыто (открывается снова после перезапуска оболочки).
    public bool Window { get => Bool("window", false); set => S["window"] = value; }

    /// Окно поверх всех окон (по умолчанию обычное).
    public bool WindowTopmost { get => Bool("windowTopmost", false); set => S["windowTopmost"] = value; }

    /// Где стоит окно: физические пиксели экрана (null — ещё не ставили).
    public (int X, int Y, int W, int H)? WindowRect
    {
        get => S["windowRect"] is JsonArray a && a.Count == 4 && a.All(n => n is JsonValue v && v.TryGetValue<int>(out _))
            ? (a[0]!.GetValue<int>(), a[1]!.GetValue<int>(), a[2]!.GetValue<int>(), a[3]!.GetValue<int>()) : null;
        set => S["windowRect"] = value is { } r ? new JsonArray(r.X, r.Y, r.W, r.H) : null;
    }

    /// Какие колонки показывать (по умолчанию все четыре).
    public List<string> Shown
    {
        get
        {
            var list = S["columns"] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToHashSet() : null;
            return Columns.Where(c => list is null || list.Contains(c)).ToList();
        }
        set => S["columns"] = new JsonArray(Columns.Where(value.Contains).Select(v => (JsonNode)v).ToArray());
    }

    /// Полоса «Готово» внизу окна.
    public bool ShowDone { get => Bool("showDone", true); set => S["showDone"] = value; }

    /// Компактные карточки.
    public bool Compact { get => Bool("compact", false); set => S["compact"] = value; }

    /// Фон окна: как во всей оболочке, стекло или тонировка.
    public string Surface
    {
        get => Str("surface") is { } s && (s == SurfaceGlass || s == SurfaceTint) ? s : SurfaceShell;
        set => S["surface"] = value;
    }

    /// Число задач «В работе» на значке в хвосте.
    public bool Badge { get => Bool("badge", true); set => S["badge"] = value; }

    /// Проект, открытый в окне.
    public string? Project { get => Str("project"); set => S["project"] = value; }

    /// Проект в окошке быстрой задачи (последний, куда добавляли).
    public string? QuickProject { get => Str("quickProject"); set => S["quickProject"] = value; }

    /// Последний выбранный эпик в каждом проекте (для быстрой задачи).
    public string? Epic(string project) => S["epics"] is JsonObject o && o[project] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public void SetEpic(string project, string? epic)
    {
        if (S["epics"] is not JsonObject o) S["epics"] = o = new JsonObject();
        if (epic is null) o.Remove(project);
        else o[project] = epic;
    }
}
