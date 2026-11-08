namespace WindowsShell.Island.Core;

/// Состав и порядок ленты (общие для всех экранов, настраиваются).
public static class RibbonOrder
{
    /// Сохранённый порядок без неизвестных и повторов; новые виджеты — в конец, в своём порядке.
    public static List<string> Normalize(IEnumerable<string>? saved, IReadOnlyList<string> known)
    {
        var list = new List<string>();
        foreach (var id in saved ?? known)
            if (known.Contains(id) && !list.Contains(id)) list.Add(id);
        foreach (var id in known)
            if (!list.Contains(id)) list.Add(id);
        return list;
    }

    /// Сдвинуть значок на by мест (−1 — левее). У края — без изменений.
    public static List<string> Move(IReadOnlyList<string> order, string id, int by)
    {
        var list = order.ToList();
        var i = list.IndexOf(id);
        var j = i + by;
        if (i < 0 || j < 0 || j >= list.Count) return list;
        list.RemoveAt(i);
        list.Insert(j, id);
        return list;
    }
}
