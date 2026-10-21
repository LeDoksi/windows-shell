using System.Text.Json;
using WindowsShell.Config;

namespace WindowsShell.Start;

/// Отметка «Новое» в Пуске (design-v2 п. 10): оболочка помнит, какие программы уже видела в
/// списке приложений. Новая — та, которой раньше не было; отметка пропадает после первого
/// запуска или через 7 дней. Самый первый раз все программы считаются старыми.
/// Файл `seen-apps.json`: программа → когда впервые увидели (старые и запущенные — пусто).
public sealed class NewApps
{
    static readonly string File = Path.Combine(ShellConfig.Dir, "seen-apps.json");
    static readonly TimeSpan Fresh = TimeSpan.FromDays(7);
    /// Столько «новых» сразу — не установка, а сбой чтения списка (неполный список при входе в Windows).
    const int Burst = 15;

    readonly object _lock = new();
    Dictionary<string, DateTime?>? _seen;

    /// Сверить со свежим списком программ (поток каталога). true — появились новые.
    public bool Update(IReadOnlyList<StartEntry> apps)
    {
        if (apps.Count == 0) return false;
        lock (_lock)
        {
            _seen ??= Load();
            var first = _seen.Count == 0;
            // Ярлык самой оболочки «Windows Shell» (создаёт она сама при первом запуске) — не новость.
            if (_seen.TryGetValue(Native.Toasts.Aumid, out var own) && own != null) { _seen[Native.Toasts.Aumid] = null; Save(); }
            var unseen = apps.Where(a => !_seen.ContainsKey(a.Target)).Select(a => a.Target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (unseen.Count == 0) return false;
            var old = first || unseen.Count > Burst;
            if (!first && old) Log.Write($"Пуск: {unseen.Count} незнакомых программ сразу — не отмечаю как новые");
            var now = DateTime.Now;
            foreach (var t in unseen) _seen[t] = old || string.Equals(t, Native.Toasts.Aumid, StringComparison.OrdinalIgnoreCase) ? null : now;
            Save();
            var fresh = unseen.Where(t => _seen[t] != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (fresh.Count > 0) Log.Write($"Пуск: новые программы: {string.Join(", ", apps.Where(a => fresh.Contains(a.Target)).Select(a => a.Title))}");
            return fresh.Count > 0;
        }
    }

    /// Новая ли (без учёта настройки).
    public bool IsNew(string target)
    {
        lock (_lock)
            return _seen != null && _seen.TryGetValue(target, out var at) && at is { } t && DateTime.Now - t < Fresh;
    }

    /// Когда впервые увидели новую (для блока «Недавно установленные»).
    public DateTime Since(string target)
    {
        lock (_lock)
            return _seen != null && _seen.TryGetValue(target, out var at) && at is { } t ? t : DateTime.MinValue;
    }

    /// Программу запустили: больше не новая.
    public void Opened(string target)
    {
        lock (_lock)
        {
            if (_seen is null || !_seen.TryGetValue(target, out var at) || at is null) return;
            _seen[target] = null;
            Save();
        }
    }

    static Dictionary<string, DateTime?> Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, DateTime?>>(System.IO.File.ReadAllText(File));
                if (data != null) return new(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) { Log.Error("Пуск: прочитать seen-apps.json", e); }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(ShellConfig.Dir);
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(_seen));
            System.IO.File.Move(tmp, File, overwrite: true);
        }
        catch (Exception e) { Log.Error("Пуск: записать seen-apps.json", e); }
    }
}
