namespace WindowsShell.Island.Core;

/// Строка распознанного текста и её место на картинке (пиксели).
public readonly record struct OcrLine(string Text, double Left, double Top, double Width, double Height);

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

    public static string Join(IEnumerable<OcrLine> lines) => Join(lines.Select(l => (l.Text, l.Top, l.Height)).ToList());

    // ---------- «Авто»: несколько языков сразу, по каждой строке лучший ----------

    static bool Cyr(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';
    static bool Lat(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    // Кириллица, похожая на латиницу: русский движок читает «Hello» как «Непо».
    const string LookAlike = "АВЕКМНОРСТУХавекмнопрстух";
    const string Edge = ".,;:!?\"'«»„“”()[]{}<>—–-…*";
    const string InnerOk = "-'’./:%_@#+&";

    /// Насколько строка похожа на ошибку распознавания (0 — чистая). Русский движок пишет
    /// английский текст кириллицей и путает буквы («Непо worId»), английский пишет русский
    /// латиницей с цифрами и значками («npv•1BeT»). Штраф: буквы двух алфавитов в одном слове,
    /// буквы вперемешку с цифрами, значки внутри слова, заглавная после строчной, в строке
    /// слова двух алфавитов, все русские слова — из «латинских» букв.
    public static double Penalty(string line)
    {
        double p = 0;
        int cyrWords = 0, latWords = 0, look = 0;
        foreach (var raw in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim(Edge.ToCharArray());
            if (w.Length == 0) continue;
            int cyr = 0, lat = 0, dig = 0, odd = 0;
            var caseJump = false;
            for (var i = 0; i < w.Length; i++)
            {
                var c = w[i];
                if (Cyr(c)) cyr++;
                else if (Lat(c)) lat++;
                else if (char.IsDigit(c)) dig++;
                else if (char.IsLetter(c)) { }
                else if (!InnerOk.Contains(c)) odd++;
                if (i > 0 && char.IsUpper(c) && char.IsLower(w[i - 1])) caseJump = true;
            }
            if (cyr > 0 && lat > 0) p += 3;
            if (cyr + lat > 0 && dig > 0) p += 2;
            p += 2 * odd;
            if (caseJump) p += 1;
            if (cyr > 0 && lat == 0)
            {
                cyrWords++;
                if (w.All(c => !Cyr(c) || LookAlike.Contains(c))) look++;
            }
            else if (lat > 0 && cyr == 0) latWords++;
        }
        if (cyrWords > 0 && latWords > 0) p += 0.5;
        if (cyrWords > 0 && look == cyrWords) p += 0.5;
        return p;
    }

    /// Свести распознавание одной картинки несколькими языками. Основа — первый вариант;
    /// для каждой его строки берём строки других вариантов на той же высоте и оставляем
    /// текст с меньшим штрафом (поровну — первый). Строки, которых в первом нет, добавляем.
    public static List<OcrLine> Merge(IReadOnlyList<IReadOnlyList<OcrLine>> variants)
    {
        if (variants.Count == 0) return [];
        var result = new List<OcrLine>();
        var used = variants.Select(v => new bool[v.Count]).ToList();
        foreach (var line in variants[0])
        {
            var best = line;
            var bestP = Penalty(line.Text);
            for (var k = 1; k < variants.Count; k++)
            {
                var same = Enumerable.Range(0, variants[k].Count).Where(i => Overlap(line, variants[k][i])).ToList();
                if (same.Count == 0) continue;
                foreach (var i in same) used[k][i] = true;
                var text = string.Join(" ", same.Select(i => variants[k][i]).OrderBy(l => l.Left).Select(l => l.Text.Trim()));
                var p = Penalty(text);
                if (p < bestP) { best = line with { Text = text }; bestP = p; }
            }
            result.Add(best);
        }
        for (var k = 1; k < variants.Count; k++)
            for (var i = 0; i < variants[k].Count; i++)
            {
                if (used[k][i]) continue;
                var extra = variants[k][i];
                if (result.Any(r => Overlap(r, extra))) continue;
                result.Add(extra);
            }
        return result.OrderBy(l => l.Top).ThenBy(l => l.Left).ToList();
    }

    /// Одна строка: середина одной — внутри высоты другой, и по ширине они пересекаются
    /// (две колонки на одной высоте — разные строки).
    static bool Overlap(OcrLine a, OcrLine b)
    {
        var ca = a.Top + a.Height / 2;
        var cb = b.Top + b.Height / 2;
        var rows = (ca >= b.Top && ca <= b.Top + b.Height) || (cb >= a.Top && cb <= a.Top + a.Height);
        return rows && a.Left < b.Left + b.Width && b.Left < a.Left + a.Width;
    }
}
