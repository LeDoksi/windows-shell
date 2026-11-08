using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WindowsShell.Island.Core;
using D = System.Drawing;
using OcrLine = WindowsShell.Island.Core.OcrLine;

namespace WindowsShell.Shots;

/// Результат распознавания для острова: картинка (PNG), откуда она, язык и текст. `Error` —
/// понятная причина, почему текста нет.
public sealed record OcrJob(byte[] Png, string Source, string Lang, string Text, string? Error);

/// Распознавание текста Windows (И6, `Windows.Media.Ocr`). Работает без интернета; языки —
/// те, для которых в Windows установлен пакет распознавания (обычно русский и английский).
/// Мелкий текст снимка экрана перед распознаванием увеличиваем (`OcrLayout.Scale`).
public static class Ocr
{
    /// Языки распознавания в Windows: (тег, название).
    public static List<(string Tag, string Name)> Languages()
    {
        try { return OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, Name(l))).ToList(); }
        catch { return []; }
    }

    static string Name(Language l)
    {
        var n = l.NativeName;
        return n.Length > 0 ? char.ToUpper(n[0]) + n[1..] : l.LanguageTag;
    }

    /// «Авто»: распознать всеми установленными языками (русский — основа) и по каждой строке
    /// взять лучший вариант (`OcrLayout.Merge`). Иначе русский движок пишет английский
    /// кириллицей («Непо worId»), а английский — русский латиницей.
    public const string Auto = "auto";

    /// «Авто», если языков несколько; иначе тот, что есть. null — распознавания нет.
    public static string? DefaultLang()
    {
        var all = Languages();
        if (all.Count > 1) return Auto;
        return SingleLang(all);
    }

    static string? SingleLang(List<(string Tag, string Name)> all)
    {
        if (all.FirstOrDefault(l => l.Tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) is { Tag: { } ru }) return ru;
        try { if (OcrEngine.TryCreateFromUserProfileLanguages() is { } e) return e.RecognizerLanguage.LanguageTag; } catch { }
        return all.Count > 0 ? all[0].Tag : null;
    }

    public static byte[] Png(D.Bitmap bmp)
    {
        using var s = new MemoryStream();
        bmp.Save(s, System.Drawing.Imaging.ImageFormat.Png);
        return s.ToArray();
    }

    /// Текст картинки. Ошибка — словами, без исключений.
    public static async Task<(string Text, string? Error)> Recognize(byte[] image, string? lang)
    {
        try
        {
            var engines = new List<OcrEngine>();
            if (lang == Auto)
            {
                // Основа — русский (или язык по умолчанию), за ним остальные; не больше трёх.
                var all = Languages();
                var first = SingleLang(all);
                foreach (var tag in all.Select(l => l.Tag).OrderBy(t => t == first ? 0 : 1).Take(3))
                    if (OcrEngine.TryCreateFromLanguage(new Language(tag)) is { } e) engines.Add(e);
            }
            else if (lang != null && OcrEngine.TryCreateFromLanguage(new Language(lang)) is { } one) engines.Add(one);
            if (engines.Count == 0 && OcrEngine.TryCreateFromUserProfileLanguages() is { } profile) engines.Add(profile);
            if (engines.Count == 0) return ("", "В Windows нет пакета распознавания текста. Добавь его в «Время и язык → Язык»: у языка «Русский» — «Оптическое распознавание символов».");

            using var mem = new InMemoryRandomAccessStream();
            using (var w = new DataWriter(mem.GetOutputStreamAt(0)))
            {
                w.WriteBytes(image);
                await w.StoreAsync();
                await w.FlushAsync();
                w.DetachStream();
            }
            mem.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(mem);
            var (pw, ph) = ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
            var k = OcrLayout.Scale(pw, ph, (int)OcrEngine.MaxImageDimension);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(pw * k)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(ph * k)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var variants = new List<IReadOnlyList<OcrLine>>();
            foreach (var engine in engines)
            {
                var result = await engine.RecognizeAsync(bitmap);
                variants.Add(result.Lines.Select(Line).ToList());
            }
            var text = OcrLayout.Join(OcrLayout.Merge(variants));
            return (text, text.Length == 0 ? "Текст не найден. Попробуй выделить область крупнее или другой язык." : null);
        }
        catch (Exception e)
        {
            Log.Error("распознавание текста", e);
            return ("", "Распознать не получилось. Подробности в журнале оболочки.");
        }
    }

    static OcrLine Line(Windows.Media.Ocr.OcrLine l)
    {
        var words = l.Words.ToList();
        if (words.Count == 0) return new OcrLine(l.Text, 0, 0, 0, 0);
        var left = words.Min(x => x.BoundingRect.X);
        var top = words.Min(x => x.BoundingRect.Y);
        var right = words.Max(x => x.BoundingRect.X + x.BoundingRect.Width);
        var height = words.Max(x => x.BoundingRect.Height);
        return new OcrLine(l.Text, left, top, right - left, height);
    }

    /// `--ocr-test`: рисует строку текста и распознаёт её каждым языком. Итог — `ocr-test.txt`.
    public const string Arg = "--ocr-test";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проверка OCR: {line}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var langs = Languages();
            Say($"языки распознавания: {(langs.Count == 0 ? "нет" : string.Join(", ", langs.Select(l => $"{l.Tag} ({l.Name})")))}; по умолчанию {DefaultLang() ?? "—"}");
            const string sample = "Привет, мир 2026";
            const string sample2 = "Hello world";
            using var bmp = new D.Bitmap(420, 70);
            using (var g = D.Graphics.FromImage(bmp))
            {
                g.Clear(D.Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var font = new D.Font("Segoe UI", 11);
                g.DrawString(sample, font, D.Brushes.Black, 8, 6);
                g.DrawString(sample2, font, D.Brushes.Black, 8, 34);
            }
            var png = Png(bmp);
            var tags = langs.Select(l => (string?)l.Tag).ToList();
            if (langs.Count > 1) tags.Insert(0, Auto);
            if (tags.Count == 0) tags.Add(null);
            foreach (var tag in tags)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (text, error) = Recognize(png, tag).GetAwaiter().GetResult();
                var flat = text.Replace('\n', ' ');
                // «Авто» должно прочитать обе строки верно; один язык — хотя бы свою.
                var ok = tag == Auto
                    ? flat.Contains("Привет, мир 2026") && flat.Contains("Hello world")
                    : tag != null && tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                        ? flat.Contains("Привет", StringComparison.OrdinalIgnoreCase) && flat.Contains("2026")
                        : flat.Contains("Hello", StringComparison.OrdinalIgnoreCase);
                Say($"{tag ?? "профиль"}: {(ok ? "OK" : "НЕТ")} за {sw.ElapsedMilliseconds} мс → «{flat}»{(error != null ? $" ({error})" : "")}");
            }
        }
        catch (Exception e) { Say($"ОШИБКА: {e}"); }
        Say("ГОТОВО");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ocr-test.txt"), report.ToString());
        return true;
    }
}
