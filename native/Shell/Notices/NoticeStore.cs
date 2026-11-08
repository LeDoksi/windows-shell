using System.Text.Json;

namespace WindowsShell.Notices;

/// Одно уведомление в нашем центре. `WinId` — id в центре Windows (0 — своё событие оболочки),
/// `App` — имя приложения, `Aumid` — кого открыть по клику.
public sealed class Notice
{
    public uint WinId { get; set; }
    public string App { get; set; } = "";
    public string Aumid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public bool Read { get; set; }
}

public sealed record NoticeGroup(string App, string Aumid, List<Notice> Items)
{
    public int Unread => Items.Count(n => !n.Read);
}

/// Что делать с новым уведомлением: показать ли пилюлю и сколько в пачке этого приложения
/// (пачка — пока пилюля видна, П7: одна пилюля с последним сообщением и «ещё N»).
public readonly record struct NoticeAdd(bool Pill, int Burst);

/// Когда показывать пилюлю. Своё «Не беспокоить» острова — никогда (тихо копим). Баннеры
/// Windows ещё показываются («Не беспокоить» Windows выключено) — тоже нет: Windows уже показала
/// баннер, второй был бы дублем; уведомление всё равно ложится в наш центр.
public static class NoticeRule
{
    public static bool Pill(bool islandQuiet, bool windowsQuiet, bool eventOn) => eventOn && !islandQuiet && windowsQuiet;
}

/// Собственный центр уведомлений (П18): группы по приложениям, новые сверху, «прочитано» при
/// открытии, очистка по приложению и всё сразу, хранение трое суток с получения. Только System.*.
public sealed class NoticeStore
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(3);
    const int MaxItems = 500;

    readonly string? _path;
    List<Notice> _items = [];
    string? _burstApp;
    int _burst;

    public NoticeStore(string? path = null) => _path = path;

    public IReadOnlyList<Notice> Items => _items;
    public bool Unread => _items.Any(n => !n.Read);
    public int UnreadCount => _items.Count(n => !n.Read);

    /// Уже есть в центре (по id Windows).
    public bool Known(uint winId) => winId != 0 && _items.Any(n => n.WinId == winId);

    /// Новое уведомление. `pill` — правило разрешает пилюлю (NoticeRule).
    public NoticeAdd Add(Notice n, bool pill)
    {
        if (Known(n.WinId)) return new NoticeAdd(false, 0);
        _items.Insert(0, n);
        Trim();
        if (!pill) return new NoticeAdd(false, 0);
        if (_burstApp == n.App) _burst++;
        else { _burstApp = n.App; _burst = 1; }
        return new NoticeAdd(true, _burst);
    }

    /// Пилюля закрылась: следующая — новая пачка.
    public void EndBurst()
    {
        _burstApp = null;
        _burst = 0;
    }

    /// Подхват при старте: то, что Windows держит в своём центре, а у нас нет (пришло, пока
    /// оболочки не было). Тихо, непрочитанным, только за последние 3 дня.
    public int Import(IEnumerable<Notice> list, DateTimeOffset now)
    {
        var n = 0;
        foreach (var item in list)
        {
            if (Known(item.WinId) || now - item.Time > Keep) continue;
            _items.Add(item);
            n++;
        }
        if (n > 0)
        {
            _items = _items.OrderByDescending(i => i.Time).ToList();
            Trim();
        }
        return n;
    }

    public void MarkAllRead()
    {
        foreach (var n in _items) n.Read = true;
    }

    /// Приложения по свежести их последнего уведомления; внутри — новые сверху.
    public List<NoticeGroup> Groups() => _items
        .GroupBy(n => n.App)
        .Select(g => new NoticeGroup(g.Key, g.First().Aumid, g.OrderByDescending(n => n.Time).ToList()))
        .OrderByDescending(g => g.Items[0].Time)
        .ToList();

    public void Clear(string app) => _items.RemoveAll(n => n.App == app);

    public void ClearAll() => _items.Clear();

    /// Старше трёх суток — долой. true — что-то убрали.
    public bool Expire(DateTimeOffset now) => _items.RemoveAll(n => now - n.Time > Keep) > 0;

    void Trim()
    {
        if (_items.Count > MaxItems) _items.RemoveRange(MaxItems, _items.Count - MaxItems);
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try { _items = JsonSerializer.Deserialize<List<Notice>>(File.ReadAllText(_path), Json) ?? []; }
        catch { _items = []; }
    }

    public void Save()
    {
        if (_path is null) return;
        var tmp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
