using System.Data.OleDb;
using System.Text;

namespace WindowsShell.Start;

/// Поиск Пуска (раздел 3.3). Программы, параметры и команды ищутся в памяти мгновенно;
/// файлы спрашиваем у индекса Windows Search в фоне и добавляем, когда придут.
public static class Search
{
    public sealed record Results(
        StartEntry? Best,
        IReadOnlyList<StartEntry> Apps,
        IReadOnlyList<StartEntry> Settings,
        IReadOnlyList<StartEntry> Files,
        IReadOnlyList<StartEntry> Commands)
    {
        public bool Empty => Best is null && Apps.Count == 0 && Settings.Count == 0 && Files.Count == 0 && Commands.Count == 0;
    }

    static string Norm(string s) => s.ToLowerInvariant().Replace('ё', 'е');

    const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,.";
    const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбю";

    /// Набрали не в той раскладке: «сьв» → «cmd», «ghjdjlybr» → «проводник».
    public static string SwapLayout(string q)
    {
        var sb = new StringBuilder(q.Length);
        foreach (var ch in q)
        {
            var i = En.IndexOf(ch);
            if (i >= 0) { sb.Append(Ru[i]); continue; }
            i = Ru.IndexOf(ch);
            sb.Append(i >= 0 ? En[i] : ch);
        }
        return sb.ToString();
    }

    /// Насколько название подходит к запросу: 0 — не подходит.
    public static int Score(string title, string q, string[]? keywords = null)
    {
        var t = Norm(title);
        if (t == q) return 1000;
        if (t.StartsWith(q)) return 800 - Math.Min(100, t.Length - q.Length);
        var words = t.Split(new[] { ' ', '-', '_', '.', '(', ')', '«', '»', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q))) return 600;
        // Первые буквы слов: «vsc» → Visual Studio Code.
        if (q.Length >= 2 && words.Length >= q.Length && string.Concat(words.Select(w => w[0])).StartsWith(q)) return 450;
        if (q.Length >= 2 && t.Contains(q)) return 300;
        if (keywords != null)
        {
            foreach (var k in keywords)
            {
                var kn = Norm(k);
                if (kn.StartsWith(q) || kn.Split(' ').Any(w => w.StartsWith(q))) return 250;
            }
        }
        return 0;
    }

    static int Best(StartEntry e, string q, string swapped, Func<StartEntry, int> usage)
    {
        var s = Score(e.Title, q, e.Keywords);
        if (s == 0 && swapped != q) s = Score(e.Title, swapped, e.Keywords) * 9 / 10;
        return s == 0 ? 0 : s + Math.Min(usage(e), 20) * 10;
    }

    static List<StartEntry> Top(IEnumerable<StartEntry> items, string q, string swapped, Func<StartEntry, int> usage, int take, out int bestScore)
    {
        var scored = items.Select(e => (e, s: Best(e, q, swapped, usage))).Where(x => x.s > 0)
            .OrderByDescending(x => x.s).ThenBy(x => x.e.Title.Length).Take(take).ToList();
        bestScore = scored.Count > 0 ? scored[0].s : 0;
        return scored.Select(x => x.e).ToList();
    }

    /// Мгновенная часть поиска (без индекса файлов). `files` — уже найденные файлы для этого запроса.
    public static Results Instant(Catalog catalog, string query, IReadOnlyList<StartEntry> recent, IReadOnlyList<StartEntry>? files, Func<StartEntry, int> usage)
    {
        var q = Norm(query.Trim());
        if (q.Length == 0) return new(null, [], [], [], []);
        var swapped = SwapLayout(q);
        var apps = Top(catalog.Apps, q, swapped, usage, 7, out var appScore);
        var settings = Top(Catalog.Settings, q, swapped, usage, 5, out var setScore);
        var commands = CommandMatches(catalog, query.Trim(), q, swapped, out var cmdScore);
        // Файлы: недавние сразу, из индекса — когда придут.
        var fileList = new List<StartEntry>();
        foreach (var f in recent) if (Best(f, q, swapped, usage) > 0) fileList.Add(f);
        foreach (var f in files ?? []) if (fileList.All(x => !string.Equals(x.Target, f.Target, StringComparison.OrdinalIgnoreCase))) fileList.Add(f);
        if (fileList.Count > 6) fileList = fileList.Take(6).ToList();

        StartEntry? best = null;
        var top = Math.Max(appScore, Math.Max(setScore, cmdScore));
        if (top > 0)
        {
            if (appScore == top) { best = apps[0]; apps.RemoveAt(0); }
            else if (setScore == top) { best = settings[0]; settings.RemoveAt(0); }
            else { best = commands[0]; commands.RemoveAt(0); }
        }
        else if (fileList.Count > 0) { best = fileList[0]; fileList.RemoveAt(0); }
        if (apps.Count > 6) apps.RemoveAt(apps.Count - 1);
        if (settings.Count > 4) settings.RemoveAt(settings.Count - 1);
        return new(best, apps, settings, fileList, commands);
    }

    /// Команды «Выполнить»: по началу имени; «ping 8.8.8.8» — команда с аргументами;
    /// путь или адрес открывается как есть.
    static List<StartEntry> CommandMatches(Catalog catalog, string raw, string q, string swapped, out int bestScore)
    {
        var list = new List<StartEntry>();
        bestScore = 0;
        var space = raw.IndexOf(' ');
        if (raw.Contains(":\\") || raw.StartsWith("\\\\") || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith('%'))
        {
            list.Add(new StartEntry(ResultKind.Command, raw, raw, "Открыть"));
            bestScore = 700;
        }
        else if (space > 0)
        {
            var name = raw[..space];
            var cmd = catalog.Commands.FirstOrDefault(c => string.Equals(c.Target, name, StringComparison.OrdinalIgnoreCase));
            if (cmd != null)
            {
                list.Add(new StartEntry(ResultKind.Command, raw, cmd.Target, "Выполнить команду") { Args = raw[(space + 1)..].Trim() });
                bestScore = 900;
            }
        }
        foreach (var c in catalog.Commands)
        {
            var n = Norm(c.Target);
            var exact = n == q || n == swapped || Path.GetFileNameWithoutExtension(n) == q;
            var prefix = q.Length >= 2 && (n.StartsWith(q) || n.StartsWith(swapped));
            var known = Catalog.IsKnownCommand(c.Target);
            var byTitle = known && q.Length >= 3 && Score(c.Subtitle, q, c.Keywords) >= 600;
            if (!exact && !prefix && !byTitle) continue;
            // Точное имя команды («cmd», «regedit») — лучшее совпадение; по началу — ниже программ.
            var s = exact ? 950 : known ? 500 : 200;
            if (list.Any(x => string.Equals(x.Target, c.Target, StringComparison.OrdinalIgnoreCase) && x.Args is null)) continue;
            list.Add(c);
            bestScore = Math.Max(bestScore, s);
            if (list.Count >= 4) break;
        }
        // Точное совпадение — первым.
        list = list.OrderByDescending(c => Norm(c.Target) == q || c.Args != null ? 1 : 0).ToList();
        return list;
    }

    // ---------- файлы ----------

    /// Файлы и папки из индекса Windows Search (как поиск в Пуске Windows). Пустой список, если
    /// индекс выключен. Выполняется в фоновом потоке.
    public static List<StartEntry> Files(string query, int take, CancellationToken cancel)
    {
        var list = new List<StartEntry>();
        var q = query.Trim().Replace("'", "''").Replace("\"", "");
        if (q.Length < 2) return list;
        try
        {
            using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties=\"Application=Windows\"");
            conn.Open();
            if (cancel.IsCancellationRequested) return list;
            var sql = $"SELECT TOP {take * 2} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemTypeText FROM SystemIndex " +
                      $"WHERE SCOPE='file:' AND CONTAINS(System.FileName, '\"{q}*\"') ORDER BY System.Search.Rank DESC, System.DateModified DESC";
            using var cmd = new OleDbCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read() && list.Count < take)
            {
                if (cancel.IsCancellationRequested) break;
                var path = r.IsDBNull(0) ? null : r.GetString(0);
                if (string.IsNullOrEmpty(path)) continue;
                // Служебное: AppData, временные файлы, ярлыки «Недавних».
                var low = path.ToLowerInvariant();
                if (low.Contains("\\appdata\\") || low.Contains("\\$recycle.bin")) continue;
                var name = r.IsDBNull(1) ? Path.GetFileName(path) : r.GetString(1);
                var folder = Directory.Exists(path);
                list.Add(new StartEntry(folder ? ResultKind.Folder : ResultKind.File, name, path, Path.GetDirectoryName(path) ?? path));
            }
        }
        catch (Exception e)
        {
            Log.Write($"поиск файлов: {e.Message}");
        }
        return list;
    }
}
