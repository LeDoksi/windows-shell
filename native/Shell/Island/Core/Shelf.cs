namespace WindowsShell.Island.Core;

/// Полка файлов (П17): список путей к оригиналам. Новые сверху, без повторов (без учёта регистра).
public static class ShelfList
{
    public static int Add(IList<string> list, IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var p in paths.Reverse())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var i = IndexOf(list, p);
            if (i >= 0) list.RemoveAt(i);
            else added++;
            list.Insert(0, p);
        }
        return added;
    }

    public static bool Remove(IList<string> list, string path)
    {
        var i = IndexOf(list, path);
        if (i < 0) return false;
        list.RemoveAt(i);
        return true;
    }

    static int IndexOf(IList<string> list, string p)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], p, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
