using WindowsShell.Island.Core;

namespace WindowsShell.Config;

/// Секция `island` конфига: остров у верхнего края (WSH-E23). По умолчанию включён и стоит
/// рядом с доком: док ничего не теряет, пока Георгий сам не уберёт значок на странице «Перенос».
public sealed class IslandSection
{
    public bool Enabled { get; set; } = true;
    /// Порядок значков ленты (null — по умолчанию).
    public List<string>? Ribbon { get; set; }
    /// Значки, убранные из ленты (события виджета от этого не выключаются).
    public List<string> Hidden { get; set; } = [];
    /// Включены ли события: "lang", "charge". Нет записи — включено.
    public Dictionary<string, bool> Events { get; set; } = [];
    /// Виджеты, которые Георгий убрал из дока кнопкой «Проверил — убрать из дока» (П24).
    public List<string> OutOfDock { get; set; } = [];
}

/// Чтение секции острова с порядком и соответствием значкам дока.
public sealed class IslandSettings(IslandSection s)
{
    /// Виджеты острова (волны 1–3), порядок по умолчанию (медиа — постоянное место слева, В3).
    public static readonly string[] Known = ["media", "charge", "net", "sound", "lang", "shots", "bin", "shelf", "perf", "kanban"];

    /// Виджеты, у которых есть старый доступ (значок в доке, у монитора ПК — окно): их можно
    /// перенести (П24). Полки в доке нет.
    public static readonly string[] Movable = ["media", "charge", "net", "sound", "lang", "shots", "bin", "perf", "kanban"];

    public static string Title(string id) => id switch
    {
        "media" => "Медиа",
        "charge" => "Заряд",
        "net" => "Сеть",
        "sound" => "Звук",
        "lang" => "Раскладка",
        "shots" => "Снимки",
        "bin" => "Корзина",
        "shelf" => "Полка",
        "perf" => "Монитор ПК",
        "kanban" => "Канбан",
        "settings" => "Настройки",
        _ => id,
    };

    /// Значок того же виджета в хвосте дока.
    public static string DockId(string id) => id switch
    {
        "net" => "network",
        "lang" => "language",
        "perf" => "perf-window",
        _ => id,
    };

    public bool Enabled => s.Enabled;

    public List<string> Order => RibbonOrder.Normalize(s.Ribbon, Known);

    /// Значки ленты по порядку, без скрытых.
    public List<string> Slots => Order.Where(id => !s.Hidden.Contains(id)).ToList();

    public bool Shown(string id) => !s.Hidden.Contains(id);

    public bool EventOn(EventKind k) => !s.Events.TryGetValue(EventKey(k), out var on) || on;

    public static string EventKey(EventKind k) => k switch
    {
        EventKind.Language => "lang",
        EventKind.Shot => "shots",
        _ => "charge",
    };

    /// Значок виджета остаётся в доке (по id дока: network, language, sound, media, charge).
    public bool InDock(string dockId) => !s.Enabled || !s.OutOfDock.Any(id => DockId(id) == dockId);
}
