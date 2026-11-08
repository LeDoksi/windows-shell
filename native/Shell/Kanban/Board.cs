namespace WindowsShell.Kanban;

/// Доска «как на сайте» (LeDoksi/kanban: columns.ts, done.ts, archive.ts, epics.ts, Board.tsx):
/// колонки и подписи, типы, «Готово» — последние пять, архив и поиск, группы эпиков. Только
/// данные, без интерфейса: тесты идут вместе с логикой острова.
public static class Board
{
    /// Колонки в порядке сайта.
    public static readonly string[] Columns = [KStatus.Hold, KStatus.Backlog, KStatus.Doing, KStatus.Waiting, KStatus.Done];

    /// Сколько закрытых видно в колонке «Готово» (DONE_SHOWN на сайте); остальное — в архиве.
    public const int DoneShown = 5;

    public static string StatusName(string status) => status switch
    {
        KStatus.Hold => "Hold",
        KStatus.Backlog => "Backlog",
        KStatus.Doing => "В работе",
        KStatus.Waiting => "Нужно от тебя",
        KStatus.Done => "Готово",
        _ => status,
    };

    /// Метка типа на карточке: задача — без метки, bug — «баг», chore — «долг».
    public static string? TypeName(string type) => type switch
    {
        "bug" => "баг",
        "chore" => "долг",
        _ => null,
    };

    /// Выбор типа (быстрая задача, карточка).
    public static readonly (string Id, string Name)[] TypeChoice = [("task", "Задача"), ("bug", "Баг"), ("chore", "Долг")];

    /// Последние закрытые проекта (свежие сверху) — то, что видно в колонке «Готово».
    public static List<KItem> RecentDone(IEnumerable<KItem> items, string project) =>
        items.Where(i => i.ProjectId == project && i.Status == KStatus.Done && !i.Archived)
            .OrderByDescending(i => i.ClosedAt ?? DateTimeOffset.MinValue)
            .Take(DoneShown)
            .ToList();

    /// Архив проекта: настоящие архивные и «Готово», не поместившиеся в колонку; свежие сверху.
    public static (List<KItem> Archived, List<KItem> Overflow) Archive(IEnumerable<KItem> items, string project, string query)
    {
        var all = items.Where(i => i.ProjectId == project).ToList();
        var shown = RecentDone(all, project).Select(i => i.Id).ToHashSet();
        var list = all.Where(i => (i.Archived || (i.Status == KStatus.Done && !shown.Contains(i.Id))) && Matches(i, query))
            .OrderByDescending(i => i.ClosedAt ?? i.ArchivedAt ?? DateTimeOffset.MinValue)
            .ToList();
        return (list.Where(i => i.Archived).ToList(), list.Where(i => !i.Archived).ToList());
    }

    static string Norm(string s) => s.ToLowerInvariant().Replace('ё', 'е').Trim();

    /// Поиск как на сайте: без регистра, ё = е, по названию и номеру.
    public static bool Matches(KItem item, string? query)
    {
        var q = Norm(query ?? "");
        return q.Length == 0 || Norm(item.Title).Contains(q) || Norm(item.Id).Contains(q);
    }

    /// Группы внутри колонки: эпики по их порядку, «без эпика» (и неизвестный эпик) последней.
    public static List<(KEpic? Epic, List<KItem> Items)> Groups(IEnumerable<KItem> items, IEnumerable<KEpic> epics)
    {
        var byId = epics.ToDictionary(e => e.Id);
        var list = items.ToList();
        var groups = list.Where(i => i.EpicId != null && byId.ContainsKey(i.EpicId))
            .GroupBy(i => i.EpicId!)
            .Select(g => (Epic: (KEpic?)byId[g.Key], Items: g.ToList()))
            .OrderBy(g => g.Epic!.Position).ThenBy(g => g.Epic!.Seq)
            .ToList();
        var loose = list.Where(i => i.EpicId is null || !byId.ContainsKey(i.EpicId)).ToList();
        if (loose.Count > 0) groups.Add((null, loose));
        return groups;
    }

    static readonly uint[] Palette =
    [
        0xFF4CC2FF, 0xFFFFB340, 0xFF7DD99B, 0xFFC79BFF, 0xFFFF8A84, 0xFF5EE0D0, 0xFFF2A7D8, 0xFFD9C36A,
    ];

    /// Цвет метки эпика: один и тот же для эпика во всех колонках и окнах (ARGB). «Без эпика» — серый.
    public static uint EpicColor(string? epicId)
    {
        if (string.IsNullOrEmpty(epicId)) return 0xFF6E6E76;
        // Номер эпика (E22) даёт соседним эпикам разные цвета; без номера — простая сумма.
        var tail = epicId[(epicId.LastIndexOf('-') + 1)..].TrimStart('E', 'e');
        var n = int.TryParse(tail, out var seq) ? seq : epicId.Sum(c => c);
        return Palette[(n % Palette.Length + Palette.Length) % Palette.Length];
    }

    /// Число «Нужно от тебя» на значке: неархивные waiting в неархивных проектах.
    public static int WaitingCount(IEnumerable<KItem> items, IEnumerable<KProject> projects)
    {
        var ids = projects.Select(p => p.Id).ToHashSet();
        return items.Count(i => !i.Archived && i.Status == KStatus.Waiting && ids.Contains(i.ProjectId));
    }

    /// Что вернуть по «Отменить» (undoPatch сайта): статус, место и исходная дата закрытия.
    public static (string Status, double Position, DateTimeOffset? ClosedAt) Undo(KItem item) => (item.Status, item.Position, item.ClosedAt);

    /// Место нового эпика (как CreateModal.tsx).
    public static double EpicPosition(int seq) => seq * 100.0;
}
