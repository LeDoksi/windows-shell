using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsShell.Clips;

public enum ClipKind { Text, Image, Files }

/// Один пункт истории буфера. Данные лежат рядом файлами `<Id>.txt|.html|.rtf|.png`.
public sealed class ClipEntry
{
    public string Id { get; set; } = "";
    public ClipKind Kind { get; set; }
    /// Когда скопировали последний раз (повтор поднимает пункт наверх).
    public DateTime Time { get; set; }
    public bool Pinned { get; set; }
    public string? Source { get; set; }
    public string Hash { get; set; } = "";
    public bool HasHtml { get; set; }
    public bool HasRtf { get; set; }
    public List<string>? Files { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// Начало текста для списка и поиска (весь текст — в файле).
    [JsonIgnore] public string? Text { get; set; }
}

/// История буфера на диске (design-v2 п. 5): `%LOCALAPPDATA%\WindowsShellNative\clipboard`,
/// только на этом компьютере. Закреплённые хранятся всегда и в своём порядке, остальное —
/// 3 дня (и не больше 500 пунктов). Список меняется только в потоке интерфейса.
public sealed class ClipStore
{
    public static readonly string DefaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsShellNative", "clipboard");
    public static readonly TimeSpan Keep = TimeSpan.FromDays(3);
    const int MaxHistory = 500;
    /// Сколько текста держим в памяти для списка и поиска.
    const int TextInMemory = 50_000;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new JsonStringEnumConverter() } };

    readonly string _dir;
    readonly object _save = new();

    public List<ClipEntry> Pinned { get; private set; } = [];
    /// Новые сверху.
    public List<ClipEntry> History { get; private set; } = [];

    public ClipStore(string? dir = null) => _dir = dir ?? DefaultDir;

    public string Dir => _dir;
    string IndexPath => Path.Combine(_dir, "index.json");
    public string DataPath(ClipEntry e, string ext) => Path.Combine(_dir, e.Id + ext);

    sealed class Index
    {
        public List<ClipEntry> Pinned { get; set; } = [];
        public List<ClipEntry> History { get; set; } = [];
    }

    /// Прочитать с диска (в фоне, до показа): список и начала текстов. Файлы без записи в списке удаляются.
    public void Load()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            if (File.Exists(IndexPath))
            {
                var index = JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath), Json) ?? new Index();
                Pinned = index.Pinned.Where(Exists).ToList();
                History = index.History.Where(Exists).ToList();
            }
            foreach (var e in Pinned.Concat(History).Where(e => e.Kind == ClipKind.Text)) e.Text = ReadText(e, TextInMemory);
            var known = Pinned.Concat(History).Select(e => e.Id).ToHashSet();
            foreach (var f in Directory.GetFiles(_dir))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                // Свежие не трогаем: их мог только что записать новый пункт, пока читали список.
                if (Path.GetFileName(f) != "index.json" && !known.Contains(name) && File.GetLastWriteTime(f) < DateTime.Now.AddMinutes(-2)) TryDelete(f);
            }
        }
        catch (Exception e)
        {
            Log.Error("буфер: чтение истории", e);
        }
    }

    bool Exists(ClipEntry e) => e.Kind switch
    {
        ClipKind.Text => File.Exists(DataPath(e, ".txt")),
        ClipKind.Image => File.Exists(DataPath(e, ".png")),
        _ => e.Files is { Count: > 0 },
    };

    /// Весь текст пункта (или начало длиной `max`).
    public string ReadText(ClipEntry e, int max = int.MaxValue)
    {
        try
        {
            if (max == int.MaxValue) return File.ReadAllText(DataPath(e, ".txt"));
            using var r = new StreamReader(DataPath(e, ".txt"), Encoding.UTF8);
            var buf = new char[max];
            var n = r.ReadBlock(buf, 0, max);
            return new string(buf, 0, n);
        }
        catch
        {
            return "";
        }
    }

    public byte[]? ReadBytes(ClipEntry e, string ext)
    {
        try { return File.ReadAllBytes(DataPath(e, ext)); }
        catch { return null; }
    }

    /// Новый пункт из снимка буфера: данные сразу пишутся в файлы (в фоне). null — не получилось.
    public ClipEntry? Prepare(ClipSnapshot s)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var e = new ClipEntry { Id = Guid.NewGuid().ToString("N")[..12], Time = DateTime.Now, Source = s.Source };
            if (s.Files != null)
            {
                e.Kind = ClipKind.Files;
                e.Files = s.Files;
                e.Hash = Hash("files\n" + string.Join("\n", s.Files).ToLowerInvariant());
            }
            else if (s.Text != null)
            {
                e.Kind = ClipKind.Text;
                // Оформление не в счёт: один и тот же текст с оформлением и без — один пункт.
                e.Hash = Hash("text\n" + s.Text);
                File.WriteAllText(DataPath(e, ".txt"), s.Text, new UTF8Encoding(false));
                if (s.Html != null) { File.WriteAllBytes(DataPath(e, ".html"), s.Html); e.HasHtml = true; }
                if (s.Rtf != null) { File.WriteAllBytes(DataPath(e, ".rtf"), s.Rtf); e.HasRtf = true; }
                e.Text = s.Text.Length > TextInMemory ? s.Text[..TextInMemory] : s.Text;
            }
            else
            {
                e.Kind = ClipKind.Image;
                int w, h;
                var png = s.Png;
                if (png != null) (w, h) = Board.PngSize(png);
                else if (s.Dib != null) png = Board.DibToPng(s.Dib, out w, out h);
                else return null;
                if (png is null) return null;
                e.Width = w;
                e.Height = h;
                e.Hash = Hash(png);
                File.WriteAllBytes(DataPath(e, ".png"), png);
            }
            return e;
        }
        catch (Exception ex)
        {
            Log.Error("буфер: запись пункта", ex);
            return null;
        }
    }

    /// Добавить подготовленный пункт. Такой уже есть — поднимается наверх (новые файлы удаляются);
    /// совпал с закреплённым — закреплённый остаётся на своём месте. Возвращает пункт в списке.
    public ClipEntry Commit(ClipEntry e)
    {
        var same = Pinned.FirstOrDefault(x => x.Hash == e.Hash) ?? History.FirstOrDefault(x => x.Hash == e.Hash);
        if (same != null)
        {
            DeleteData(e);
            if (!same.Pinned)
            {
                same.Time = e.Time;
                same.Source = e.Source ?? same.Source;
                History.Remove(same);
                History.Insert(0, same);
            }
            Save();
            return same;
        }
        History.Insert(0, e);
        if (!Trim()) Save();
        return e;
    }

    /// Вставили пункт из истории: он поднимается наверх, как будто его скопировали снова.
    public void Touch(ClipEntry e)
    {
        if (e.Pinned || !History.Remove(e)) return;
        e.Time = DateTime.Now;
        History.Insert(0, e);
        Save();
    }

    public void SetPinned(ClipEntry e, bool pinned)
    {
        if (e.Pinned == pinned) return;
        e.Pinned = pinned;
        if (pinned)
        {
            History.Remove(e);
            Pinned.Add(e);
        }
        else
        {
            Pinned.Remove(e);
            // Открепили — в историю на место по времени; старше 3 дней — сразу наверх, чтобы не пропал.
            if (DateTime.Now - e.Time > Keep) e.Time = DateTime.Now;
            var at = History.FindIndex(x => x.Time < e.Time);
            History.Insert(at < 0 ? History.Count : at, e);
        }
        Save();
    }

    /// Перетащили закреплённый на место `index`.
    public void MovePinned(ClipEntry e, int index)
    {
        if (!Pinned.Remove(e)) return;
        Pinned.Insert(Math.Clamp(index, 0, Pinned.Count), e);
        Save();
    }

    public void Remove(ClipEntry e)
    {
        if (!Pinned.Remove(e) && !History.Remove(e)) return;
        DeleteData(e);
        Save();
    }

    /// Очистить историю; закреплённые остаются.
    public void ClearHistory()
    {
        var old = History;
        History = [];
        foreach (var e in old) DeleteData(e);
        Save();
    }

    /// Убрать старше 3 дней. true — что-то убрали.
    public bool Trim()
    {
        var limit = DateTime.Now - Keep;
        var old = History.Where((e, i) => e.Time < limit || i >= MaxHistory).ToList();
        if (old.Count == 0) return false;
        History = History.Except(old).ToList();
        foreach (var e in old) DeleteData(e);
        Save();
        return true;
    }

    /// Подготовленный пункт не понадобился: удалить его файлы.
    public void Discard(ClipEntry e) => DeleteData(e);

    void DeleteData(ClipEntry e)
    {
        foreach (var ext in new[] { ".txt", ".html", ".rtf", ".png" }) TryDelete(DataPath(e, ext));
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    /// Записать список (атомарно: во временный файл, потом подмена).
    public void Save()
    {
        var index = new Index { Pinned = [.. Pinned], History = [.. History] };
        lock (_save)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(index, Json));
                File.Move(tmp, IndexPath, overwrite: true);
            }
            catch (Exception e)
            {
                Log.Error("буфер: запись истории", e);
            }
        }
    }

    static string Hash(string s) => Hash(Encoding.UTF8.GetBytes(s));
    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
}
