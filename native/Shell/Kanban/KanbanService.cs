using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using WindowsShell.Apps;
using WindowsShell.Config;
using WindowsShell.Native;

namespace WindowsShell.Kanban;

/// Виджет «Канбан» (design-v2 п. 9): тот же канбан, что на сайте, без копии. Значок в хвосте
/// (быстрая задача) и отдельное окно с доской. Данные читаются заново раз в 15 с, пока окно
/// открыто, и раз в 2 минуты для числа на значке; свои изменения видны сразу, а пишутся в
/// базу в фоне (не записалось — карточка возвращается и видна ошибка).
public sealed class KanbanService
{
    const int FastSeconds = 15, SlowSeconds = 120;

    readonly DispatcherQueue _queue;
    readonly ShellConfig _config;
    readonly DispatcherQueueTimer _timer;
    /// Свои изменения, которые ещё пишутся: поверх свежих данных, пока база не ответила.
    readonly Dictionary<string, Action<KItem>> _pending = [];
    KanbanWindow? _window;
    CancellationTokenSource? _google;
    bool _loading, _again;

    public KanbanApi Api { get; } = new();
    public KanbanSettings Settings => new(_config.Widget("kanban"));
    public bool Enabled => _config.WidgetEnabled("kanban");

    public List<KProject> Projects { get; private set; } = [];
    public List<KEpic> Epics { get; private set; } = [];
    public List<KItem> Items { get; private set; } = [];

    /// Когда данные читались последний раз (null — ещё ни разу).
    public DateTime? LoadedAt { get; private set; }
    public bool Loading => _loading;

    /// Последняя ошибка чтения или записи (null — всё хорошо).
    public string? Error { get; private set; }

    /// Ждём, пока браузер вернётся со входом через Google.
    public bool WaitingForGoogle => _google != null;
    public string? SignInError { get; private set; }

    /// Данные, вход или ошибка поменялись (поток интерфейса).
    public event Action? Changed;

    /// Открыть страницу настроек виджета.
    public Action OpenSettings { get; set; } = () => { };

    /// Показать меню оболочки в точке экрана (правый клик в окне канбана).
    public Action<int, int, List<Ui.MenuItem>> ShowMenu { get; set; } = (_, _, _) => { };

    public KanbanService(DispatcherQueue queue, ShellConfig config, MessageWindow messages)
    {
        _queue = queue;
        _config = config;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(SlowSeconds);
        _timer.Tick += (_, _) => _ = Refresh();
        Api.SessionChanged += () => _queue.TryEnqueue(() =>
        {
            if (!Api.SignedIn)
            {
                Projects = [];
                Epics = [];
                Items = [];
                LoadedAt = null;
                Error = null;
            }
            else _ = Refresh();
            Changed?.Invoke();
        });
        // После сна — сразу свежие задачи.
        messages.Resumed += () => { if (_timer.IsRunning) _ = Refresh(); };
    }

    /// Число «Нужно от тебя» на значке (П10): неархивные waiting в неархивных проектах.
    public int Waiting => Board.WaitingCount(Items, Projects);

    /// Число задач «В работе» во всех проектах.
    public int Doing => Items.Count(i => !i.Archived && i.Status == KStatus.Doing && Projects.Any(p => p.Id == i.ProjectId));

    /// Зовётся при каждой смене конфига и при запуске.
    public void Apply()
    {
        if (!Enabled)
        {
            _timer.Stop();
            if (_window is { } w) { _window = null; w.Close(); }
            return;
        }
        var s = Settings;
        if (s.Window && _window is null)
        {
            _window = new KanbanWindow(this, _config);
            _window.Closed += () => { _window = null; Pace(); };
        }
        else if (!s.Window && _window is { } w)
        {
            _window = null;
            w.Close();
        }
        _window?.Apply(s);
        var first = !_timer.IsRunning;
        Pace();
        if (first && LoadedAt is null) _ = Refresh();
    }

    /// Чаще, пока окно открыто.
    void Pace()
    {
        var interval = TimeSpan.FromSeconds(_window != null ? FastSeconds : SlowSeconds);
        if (_timer.Interval != interval) _timer.Interval = interval;
        if (Enabled && !_timer.IsRunning) _timer.Start();
    }

    /// Открыть окно канбана (уже открыто — вывести вперёд).
    public void OpenWindow(string? project = null)
    {
        if (project != null) _config.Update(_ => Settings.Project = project);
        if (_window is { } w) { w.Activate(project); return; }
        _config.Update(c =>
        {
            var e = c.Widgets.FirstOrDefault(x => x.Id == "kanban");
            if (e != null) e.Enabled = true;
            Settings.Window = true;
        });
        _window?.Activate(project);
    }

    public void OpenSite() => Launcher.Open(KanbanApi.Site);

    /// Куда добавляли быструю задачу: в следующий раз окошко откроется там же.
    public void RememberQuick(string project, string? epic) => _config.Update(_ =>
    {
        var s = Settings;
        s.QuickProject = project;
        s.SetEpic(project, epic);
    });

    /// Проект, открытый в окне.
    public void RememberProject(string project) => _config.Update(_ => Settings.Project = project);

    // ---------- вход ----------

    public async void SignInGoogle()
    {
        if (_google != null) return;
        _google = new CancellationTokenSource();
        SignInError = null;
        Changed?.Invoke();
        try
        {
            await Api.SignInGoogle(Launcher.Open, _google.Token);
            Toasts.Show("Канбан подключён", $"Вошли как {Api.Email}", silent: true);
        }
        catch (OperationCanceledException) { }
        catch (KanbanException e) { SignInError = e.Message; }
        catch (Exception e)
        {
            Log.Write($"канбан: вход через Google: {e}");
            SignInError = "Вход не получился, подробности в журнале оболочки.";
        }
        finally
        {
            _google?.Dispose();
            _google = null;
            Changed?.Invoke();
        }
    }

    public void CancelGoogle() => _google?.Cancel();

    /// Вход почтой и паролем. null — вошли, иначе текст ошибки.
    public async Task<string?> SignInPassword(string email, string password)
    {
        if (email.Trim().Length == 0 || password.Length == 0) return "Введи почту и пароль.";
        try
        {
            await Api.SignInPassword(email, password);
            SignInError = null;
            return null;
        }
        catch (KanbanException e) { return e.Message; }
        catch (Exception e)
        {
            Log.Write($"канбан: вход почтой: {e}");
            return "Вход не получился, подробности в журнале оболочки.";
        }
    }

    public async void SignOut() => await Api.SignOut();

    // ---------- чтение ----------

    /// Перечитать проекты, эпики и задачи.
    public async Task Refresh()
    {
        if (!Enabled || !Api.SignedIn) return;
        if (_loading) { _again = true; return; }
        _loading = true;
        try
        {
            var projects = Api.Projects();
            var epics = Api.Epics();
            var items = Api.Items();
            await Task.WhenAll(projects, epics, items);
            var list = items.Result;
            foreach (var item in list)
                if (_pending.TryGetValue(item.Id, out var change)) change(item);
            Projects = projects.Result;
            Epics = epics.Result;
            Items = list;
            LoadedAt = DateTime.Now;
            Error = null;
        }
        catch (KanbanException e) { Error = e.Message; }
        catch (Exception e)
        {
            Log.Write($"канбан: чтение: {e}");
            Error = "Не удалось прочитать канбан, подробности в журнале оболочки.";
        }
        finally
        {
            _loading = false;
            Changed?.Invoke();
        }
        if (_again) { _again = false; _ = Refresh(); }
    }

    public KProject? Project(string? id) => Projects.FirstOrDefault(p => p.Id == id);
    public KEpic? Epic(string? id) => id is null ? null : Epics.FirstOrDefault(e => e.Id == id);
    public KItem? Item(string id) => Items.FirstOrDefault(i => i.Id == id);

    /// Эпики проекта, над которыми идёт работа.
    public List<KEpic> ActiveEpics(string project) =>
        Epics.Where(e => e.ProjectId == project && e.Status == "active").OrderBy(e => e.Seq).ToList();

    /// Эпики, куда имеет смысл добавлять задачи: «active» и не всё в них сделано (или пусто).
    public List<KEpic> OpenEpics(string project) => ActiveEpics(project).Where(e =>
    {
        var (done, total) = Progress(project, e.Id);
        return total == 0 || done < total;
    }).ToList();

    /// Сделано / всего: архивные «Готово» считаются, архивные недоделанные (брошенные) — нет.
    public (int Done, int Total) Progress(string project, string? epic = null)
    {
        int done = 0, total = 0;
        foreach (var i in Items)
        {
            if (i.ProjectId != project || (epic != null && i.EpicId != epic)) continue;
            if (i.Status == KStatus.Done) { done++; total++; }
            else if (!i.Archived) total++;
        }
        return (done, total);
    }

    public int DoneThisWeek(string project)
    {
        var since = DateTimeOffset.Now.AddDays(-7);
        return Items.Count(i => i.ProjectId == project && i.Status == KStatus.Done && i.ClosedAt is { } c && c >= since);
    }

    /// Карточки колонки по порядку; «Готово» — свежие сверху.
    public List<KItem> Column(string project, string status)
    {
        var q = Items.Where(i => i.ProjectId == project && i.Status == status && !i.Archived);
        return status == KStatus.Done
            ? q.OrderByDescending(i => i.ClosedAt ?? i.UpdatedAt ?? DateTimeOffset.MinValue).ToList()
            : q.OrderBy(i => i.Position).ThenBy(i => i.Seq).ToList();
    }

    // ---------- запись ----------

    /// Быстрая задача в бэклог.
    public async Task<KItem> Create(string project, string title, string? epic, string type = "task")
    {
        var p = Project(project) ?? throw new KanbanException("Проект не найден, обнови канбан.");
        var item = await Api.Create(p, title.Trim(), epic, type);
        if (Items.All(i => i.Id != item.Id)) Items = [.. Items, item];
        Error = null;
        Changed?.Invoke();
        Log.Write($"канбан: создана {item.Id}");
        return item;
    }

    /// Задача с новым эпиком одним действием (П12): сначала эпик — не создался, задачи нет
    /// (исключение, черновик остаётся у вызывающего); потом задача в этом эпике.
    public async Task<KItem> CreateWithEpic(string project, string title, string type, string? epic, string? newEpicTitle)
    {
        if (newEpicTitle is { } et && et.Trim().Length > 0)
        {
            var p = Project(project) ?? throw new KanbanException("Проект не найден, обнови канбан.");
            var created = await Api.CreateEpic(p, et.Trim());
            if (Epics.All(e => e.Id != created.Id)) Epics = [.. Epics, created];
            Log.Write($"канбан: создан эпик {created.Id}");
            epic = created.Id;
        }
        return await Create(project, title, epic, type);
    }

    /// Что вернуть по «Отменить» после смены статуса (undoPatch сайта).
    public sealed record Undo(KItem Item, string Status, double Position, DateTimeOffset? ClosedAt);

    /// Карточку перевели в другую колонку из острова или окна: показать «Отменить».
    public event Action<KItem, Undo>? Moved;

    /// «Готово» одной кнопкой (П11). Возвращает, что вернуть по «Отменить».
    public Undo Done(KItem item) => MoveWithUndo(item, KStatus.Done);

    /// Перевести с «Отменить» (галочка, «Вернуть в работу»).
    public Undo MoveWithUndo(KItem item, string status)
    {
        var (st, pos, closed) = Board.Undo(item);
        var undo = new Undo(item, st, pos, closed);
        var column = Column(item.ProjectId, status);
        var position = status == KStatus.Done ? item.Position : PositionAt(column, column.Count);
        _ = Move(item, status, position);
        Log.Write($"канбан: {item.Id} → {Board.StatusName(status)}");
        Moved?.Invoke(item, undo);
        return undo;
    }

    /// «Отменить»: статус, место и исходная дата закрытия как были.
    public Task UndoMove(Undo u)
    {
        var fields = new JsonObject { ["status"] = u.Status, ["position"] = u.Position, ["closed_at"] = u.ClosedAt?.ToString("O") };
        Log.Write($"канбан: {u.Item.Id} — отменено, снова {Board.StatusName(u.Status)}");
        return Change(u.Item, fields, i => { i.Status = u.Status; i.Position = u.Position; i.ClosedAt = u.ClosedAt; });
    }

    public Task SetType(KItem item, string type) =>
        Change(item, new JsonObject { ["type"] = type }, i => i.Type = type);

    /// В архив (как на сайте — у закрытых).
    public Task Archive(KItem item)
    {
        var at = DateTimeOffset.UtcNow;
        return Change(item, new JsonObject { ["archived_at"] = at.ToString("O") }, i => i.ArchivedAt = at);
    }

    /// Вернуть из архива (как на сайте: в «В работе»).
    public Task Restore(KItem item)
    {
        var column = Column(item.ProjectId, KStatus.Doing);
        var position = PositionAt(column, column.Count);
        return Change(item, new JsonObject { ["status"] = KStatus.Doing, ["archived_at"] = null, ["closed_at"] = null, ["position"] = position },
            i => { i.Status = KStatus.Doing; i.ArchivedAt = null; i.ClosedAt = null; i.Position = position; });
    }

    /// Перенести карточку: статус и место в колонке.
    public Task Move(KItem item, string status, double position)
    {
        var fields = new JsonObject { ["status"] = status, ["position"] = position };
        var closed = item.ClosedAt;
        if (status != item.Status)
        {
            closed = status == KStatus.Done ? DateTimeOffset.UtcNow : null;
            fields["closed_at"] = closed?.ToString("O");
        }
        return Change(item, fields, i => { i.Status = status; i.Position = position; i.ClosedAt = closed; });
    }

    public Task Rename(KItem item, string title) =>
        Change(item, new JsonObject { ["title"] = title.Trim() }, i => i.Title = title.Trim());

    public Task SetEpic(KItem item, string? epic) =>
        Change(item, new JsonObject { ["epic_id"] = epic }, i => i.EpicId = epic);

    /// Своё изменение видно сразу; не записалось — откатываем и показываем ошибку.
    async Task Change(KItem item, JsonObject fields, Action<KItem> apply)
    {
        var before = Copy(item);
        apply(item);
        _pending[item.Id] = apply;
        Changed?.Invoke();
        try
        {
            await Api.Update(item.Id, fields);
            Error = null;
        }
        catch (Exception e)
        {
            if (e is not KanbanException) Log.Write($"канбан: запись {item.Id}: {e}");
            if (Item(item.Id) is { } now) RollBack(now, before);
            Error = e is KanbanException k ? k.Message : "Изменение не сохранилось, подробности в журнале оболочки.";
        }
        finally
        {
            if (_pending.TryGetValue(item.Id, out var a) && a == apply) _pending.Remove(item.Id);
            Changed?.Invoke();
        }
    }

    /// Описание и шаги (пишутся сразу, без отката: поле остаётся с тем, что ввели).
    public async Task<string?> Save(string id, JsonObject fields)
    {
        try
        {
            await Api.Update(id, fields);
            return null;
        }
        catch (KanbanException e) { return e.Message; }
        catch (Exception e)
        {
            Log.Write($"канбан: запись {id}: {e}");
            return "Не сохранилось, подробности в журнале оболочки.";
        }
    }

    static KItem Copy(KItem i) => new()
    {
        Id = i.Id, Seq = i.Seq, ProjectId = i.ProjectId, EpicId = i.EpicId, Type = i.Type, Title = i.Title,
        Status = i.Status, Position = i.Position, CreatedBy = i.CreatedBy, UpdatedAt = i.UpdatedAt, ClosedAt = i.ClosedAt, ArchivedAt = i.ArchivedAt,
    };

    static void RollBack(KItem to, KItem from)
    {
        to.Type = from.Type;
        to.ArchivedAt = from.ArchivedAt;
        to.EpicId = from.EpicId;
        to.Title = from.Title;
        to.Status = from.Status;
        to.Position = from.Position;
        to.ClosedAt = from.ClosedAt;
    }

    /// Место для карточки между соседями в колонке (позиции — дробные, как на сайте).
    public static double PositionAt(List<KItem> column, int index)
    {
        if (column.Count == 0) return 1000;
        if (index <= 0) return column[0].Position - 100;
        if (index >= column.Count) return column[^1].Position + 100;
        return (column[index - 1].Position + column[index].Position) / 2;
    }
}
