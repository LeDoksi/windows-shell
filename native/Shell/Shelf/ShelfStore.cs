using System.Text.Json;
using Microsoft.UI.Dispatching;
using WindowsShell.Island.Core;

namespace WindowsShell.Shelf;

/// Полка файлов (П17): ссылки на оригиналы (пути), не копии. Живёт до ручной очистки и
/// переживает перезапуск: `shelf.json` рядом с конфигом. Убрать с полки — файл не трогаем.
public sealed class ShelfStore
{
    static readonly string FilePath = Path.Combine(Config.ShellConfig.Dir, "shelf.json");

    readonly DispatcherQueue _queue;
    readonly List<string> _paths;

    public event Action? Changed;

    public ShelfStore(DispatcherQueue queue)
    {
        _queue = queue;
        _paths = Load();
    }

    public IReadOnlyList<string> Paths => _paths;

    public int Count => _paths.Count;

    public int Add(IEnumerable<string> paths)
    {
        var n = ShelfList.Add(_paths, paths);
        Save();
        Log.Write($"полка: +{n}, всего {_paths.Count}");
        return n;
    }

    public void Remove(string path)
    {
        if (ShelfList.Remove(_paths, path)) Save();
    }

    public void Clear()
    {
        if (_paths.Count == 0) return;
        _paths.Clear();
        Save();
        Log.Write("полка: очищена");
    }

    static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception e) { Log.Error("полка: прочитать", e); }
        return [];
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Атомарно: сначала во временный файл, потом подмена (оболочку могут убить посреди записи).
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_paths));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) { Log.Error("полка: сохранить", e); }
        _queue.TryEnqueue(() => Changed?.Invoke());
    }
}
