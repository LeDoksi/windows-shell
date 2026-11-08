namespace WindowsShell.Island.Core;

/// Подготовка к распознаванию текста (И6) и сборка результата. Только System.*.
public static class OcrLayout
{
    /// Во сколько раз увеличить картинку: распознавание Windows плохо видит мелкий текст
    /// (снимок области экрана с 12-пиксельным шрифтом). Маленькую — ×3, среднюю — ×2, большую
    /// не трогаем. Не больше `max` по любой стороне.
    public static double Scale(int w, int h, int max)
    {
        if (w <= 0 || h <= 0) return 1;
        var big = Math.Max(w, h);
        double k = big < 600 ? 3 : big < 1600 ? 2 : 1;
        k = Math.Min(k, max / (double)big);
        return Math.Max(1, k);
    }

    /// Строки сверху вниз через перевод строки; заметный пробел между строками (больше
    /// высоты строки) — абзац, пустая строка.
    public static string Join(IReadOnlyList<(string Text, double Top, double Height)> lines)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Top).ToList();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0)
            {
                var prev = list[i - 1];
                var gap = list[i].Top - (prev.Top + prev.Height);
                sb.Append(gap > Math.Max(prev.Height, list[i].Height) ? "\n\n" : "\n");
            }
            sb.Append(list[i].Text.Trim());
        }
        return sb.ToString();
    }
}
