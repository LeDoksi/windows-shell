using System.Text.Json.Serialization;

namespace WindowsShell.Kanban;

/// Проект канбана (таблица projects).
public sealed class KProject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public double Position { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

/// Эпик (таблица epics): id вида WSH-E21, статус active · done · archived.
public sealed class KEpic
{
    public string Id { get; set; } = "";
    public int Seq { get; set; }
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "active";
    public double Position { get; set; }

    /// «E21» из «WSH-E21».
    [JsonIgnore]
    public string Short => Id.LastIndexOf('-') is var i and >= 0 ? Id[(i + 1)..] : Id;
}

/// Задача (таблица items) без описания и шагов: их читаем, только когда карточку открыли.
public sealed class KItem
{
    public string Id { get; set; } = "";
    public int Seq { get; set; }
    public string ProjectId { get; set; } = "";
    public string? EpicId { get; set; }
    public string Type { get; set; } = "task";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "backlog";
    public double Position { get; set; }
    public string CreatedBy { get; set; } = "me";
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    [JsonIgnore]
    public bool Archived => ArchivedAt != null;
}

/// Шаг задачи (items.checklist).
public sealed class KCheck
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// Описание и шаги открытой карточки.
public sealed class KDetails
{
    public string? Body { get; set; }
    public List<KCheck> Checklist { get; set; } = [];
}

/// Комментарий (таблица comments): автор me — Георгий, claude — Claude.
public sealed class KComment
{
    public long Id { get; set; }
    public string ItemId { get; set; } = "";
    public string Author { get; set; } = "me";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// Статусы канбана: как на сайте.
public static class KStatus
{
    public const string Backlog = "backlog", Hold = "hold", Doing = "doing", Waiting = "waiting", Done = "done";
    public static readonly string[] All = [Doing, Waiting, Backlog, Hold, Done];

    public static string Name(string status) => status switch
    {
        Doing => "В работе",
        Waiting => "Ждёт",
        Backlog => "Бэклог",
        Hold => "Пауза",
        Done => "Готово",
        _ => status,
    };
}

/// Ошибка, которую можно показать как есть (по-русски).
public sealed class KanbanException(string message, bool signedOut = false, bool conflict = false) : Exception(message)
{
    /// Вход больше не действует: нужно войти снова.
    public bool SignedOut { get; } = signedOut;
    /// Такая запись уже есть (номер задачи заняли одновременно).
    public bool Conflict { get; } = conflict;
}
