using System.Text;
using System.Text.RegularExpressions;

namespace WindowsShell.Clips;

/// Преобразования текста из буфера (концепция, «Буфер»): неверная раскладка, регистр, лишние
/// переносы. Только содержимое буфера; применяет окошко Win+V после просмотра «было → стало».
/// Только System.*.
public static partial class TextFix
{
    // Клавиши ЙЦУКЕН ↔ QWERTY (Windows, раскладки «Русская» и «США»): без Shift и с Shift.
    const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?@#$^&|";
    const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,\"№;:?/";

    static readonly Dictionary<char, char> EnToRu = Map(En, Ru);
    static readonly Dictionary<char, char> RuToEn = Map(Ru, En);

    static Dictionary<char, char> Map(string from, string to)
    {
        var d = new Dictionary<char, char>();
        for (var i = 0; i < from.Length; i++) d.TryAdd(from[i], to[i]);
        return d;
    }

    static bool Cyr(char c) => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё';
    static bool Lat(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// Набрано не в той раскладке: латиницы больше — переводим в русскую, иначе — в английскую.
    public static string Layout(string text)
    {
        var lat = text.Count(Lat);
        var cyr = text.Count(Cyr);
        var map = lat >= cyr ? EnToRu : RuToEn;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(map.TryGetValue(c, out var r) ? r : c);
        return sb.ToString();
    }

    public static string Upper(string text) => text.ToUpperInvariant();

    public static string Lower(string text) => text.ToLowerInvariant();

    /// Случайно нажатый Caps Lock: «пРИВЕТ, мИР» → «Привет, мир».
    public static string SwapCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(char.IsUpper(c) ? char.ToLowerInvariant(c) : char.IsLower(c) ? char.ToUpperInvariant(c) : c);
        return sb.ToString();
    }

    /// Как в предложении: всё строчными, первая буква каждого предложения — заглавная.
    public static string Sentence(string text)
    {
        var sb = new StringBuilder(text.Length);
        var start = true;
        foreach (var c0 in text)
        {
            var c = char.ToLowerInvariant(c0);
            if (char.IsLetter(c))
            {
                sb.Append(start ? char.ToUpperInvariant(c) : c);
                start = false;
            }
            else
            {
                sb.Append(c);
                if (c is '.' or '!' or '?' or '\n') start = true;
            }
        }
        return sb.ToString();
    }

    /// Лишние переносы (текст из PDF, письма): строки внутри абзаца склеиваются через пробел,
    /// «сло-\nво» → «слово», пустая строка остаётся границей абзаца, двойные пробелы — один.
    public static string JoinLines(string text)
    {
        var paragraphs = ParagraphSplit().Split(Normalize(text).Trim('\n'));
        var parts = paragraphs.Select(p =>
        {
            var joined = Hyphen().Replace(p.Trim(), "$1");
            joined = joined.Replace('\n', ' ');
            return Spaces().Replace(joined, " ").Trim();
        }).Where(p => p.Length > 0);
        return string.Join("\n\n", parts);
    }

    /// Всё одной строкой.
    public static string OneLine(string text)
    {
        var t = Hyphen().Replace(Normalize(text).Trim(), "$1").Replace('\n', ' ');
        return Spaces().Replace(t, " ").Trim();
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');

    [GeneratedRegex(@"\n[ ]*\n+")] private static partial Regex ParagraphSplit();
    [GeneratedRegex(@"(\p{L})-[ ]*\n[ ]*(?=\p{Ll})")] private static partial Regex Hyphen();
    [GeneratedRegex(@"[ ]{2,}|[ ]*\n[ ]*")] private static partial Regex SpacesAndBreaks();
    [GeneratedRegex(@"[ ]{2,}")] private static partial Regex Spaces();

    /// Варианты, которые что-то меняют, в постоянном порядке.
    public static List<(string Id, string Title, string Result)> Options(string text)
    {
        var list = new List<(string, string, string)>();
        void Add(string id, string title, string result)
        {
            if (result != text && list.All(o => o.Item3 != result)) list.Add((id, title, result));
        }
        var lat = text.Count(Lat);
        var cyr = text.Count(Cyr);
        if (lat + cyr > 0) Add("layout", lat >= cyr ? "Раскладка → русская" : "Раскладка → английская", Layout(text));
        if (text.Any(char.IsUpper) && text.Any(char.IsLower)) Add("swap", "Caps Lock: пРИВЕТ → Привет", SwapCase(text));
        Add("sentence", "Как в предложении", Sentence(text));
        Add("lower", "все строчные", Lower(text));
        Add("upper", "ВСЕ ЗАГЛАВНЫЕ", Upper(text));
        if (text.Contains('\n') || text.Contains('\r')) Add("join", "Убрать лишние переносы", JoinLines(text));
        if (text.Contains('\n') || text.Contains('\r') || SpacesAndBreaks().IsMatch(text)) Add("oneline", "Одной строкой", OneLine(text));
        return list;
    }

    /// Какой вариант выбрать сразу. Раскладка — если латинские слова похожи на русский текст,
    /// набранный в английской раскладке (знаки ;[]',. внутри слов или слова без гласных), или
    /// русские — на английский в русской. Caps Lock — если строчные буквы почти только в начале слов.
    public static string? Suggest(string text)
    {
        var words = Words().Matches(text).Select(m => m.Value).Where(w => w.Length >= 2).ToList();
        if (words.Count == 0) return text.Contains('\n') ? "join" : null;
        var latWords = words.Where(w => w.Any(Lat) && !w.Any(Cyr)).ToList();
        var cyrWords = words.Where(w => w.Any(Cyr) && !w.Any(Lat)).ToList();
        if (latWords.Count >= cyrWords.Count && latWords.Count > 0)
        {
            var odd = latWords.Count(w => w.Trim(';', '\'', '[', ']', ',', '.', '`').IndexOfAny([';', '\'', '[', ']', ',', '.', '`']) >= 0
                || !w.Any(c => "aeiouyAEIOUY".Contains(c)));
            if (odd * 2 >= latWords.Count) return "layout";
        }
        else if (cyrWords.Count > 0)
        {
            // Английские слова в русской раскладке: «руддщ», «цщкдв» — сочетания, которых в русском почти нет.
            var odd = cyrWords.Count(w => RuOdd().IsMatch(w.ToLowerInvariant()));
            if (odd * 2 >= cyrWords.Count) return "layout";
        }
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count >= 4)
        {
            var wordStartsLower = words.Count(w => char.IsLower(w[0]) && w.Skip(1).Where(char.IsLetter).All(char.IsUpper) && w.Skip(1).Any(char.IsLetter));
            if (wordStartsLower * 2 >= words.Count) return "swap";
        }
        if (text.Contains('\n')) return "join";
        return null;
    }

    [GeneratedRegex(@"[\p{L};'\[\],.`]+")] private static partial Regex Words();
    // Сочетания, редкие в русских словах, но частые у английских в русской раскладке.
    [GeneratedRegex(@"дд|щщ|цщ|щк|ыу|кщ|ъ$|^ь|ь[аоуыэ]|[йцкнгшщзхфвпрлджчсмтб]{5}|ы[аоуэ]|^ы")] private static partial Regex RuOdd();
}
