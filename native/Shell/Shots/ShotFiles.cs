using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowsShell.Shots;

public enum ShotKind { Image, Video, Gif }

/// Снимок или запись из истории: файл и когда сделан (из имени файла).
public sealed record ShotItem(string Path, DateTime Time, ShotKind Kind)
{
    public bool IsImage => Kind == ShotKind.Image;
    public string Name => System.IO.Path.GetFileName(Path);
}

/// Где лежат снимки и записи (design-v2 п. 4). Снимки — в своей папке оболочки, записи — в
/// «Видео\Записи экрана». Трогаем только свои файлы: имя «Снимок_2026-10-21_17-20-04.png» или
/// «Запись_….mp4/.gif» (у «Ножниц» Windows имена другие), поэтому автоудаление чужое не задевает.
public static partial class ShotFiles
{
    public static readonly string ShotsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Windows Shell");
    public static readonly string VideosDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Записи экрана");

    /// Сколько хранится история: старше — удаляется насовсем (утверждено: «снял и отправил»).
    public static readonly TimeSpan Keep = TimeSpan.FromDays(3);

    const string Stamp = "yyyy-MM-dd_HH-mm-ss";

    [GeneratedRegex(@"^(Снимок|Запись)_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(-\d+)?\.(png|mp4|gif)$", RegexOptions.IgnoreCase)]
    private static partial Regex OurName();

    /// Новое имя файла: `Снимок_<время>.png`; в ту же секунду второй — с «-2».
    public static string NewPath(ShotKind kind)
    {
        var (dir, prefix, ext) = kind switch
        {
            ShotKind.Image => (ShotsDir, "Снимок", ".png"),
            ShotKind.Gif => (VideosDir, "Запись", ".gif"),
            _ => (VideosDir, "Запись", ".mp4"),
        };
        Directory.CreateDirectory(dir);
        var stem = $"{prefix}_{DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture)}";
        var path = Path.Combine(dir, stem + ext);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{stem}-{n}{ext}");
        return path;
    }

    /// GIF рядом с записью: то же имя, другое расширение.
    public static string GifFor(string video) => Path.ChangeExtension(video, ".gif");

    /// Наш ли это файл (только такие показываем и удаляем).
    public static ShotItem? Parse(string path)
    {
        var m = OurName().Match(Path.GetFileName(path));
        if (!m.Success) return null;
        if (!DateTime.TryParseExact(m.Groups[2].Value, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        var kind = m.Groups[4].Value.ToLowerInvariant() switch
        {
            "png" => ShotKind.Image,
            "gif" => ShotKind.Gif,
            _ => ShotKind.Video,
        };
        return new ShotItem(path, time, kind);
    }

    /// Вся история, новые сверху.
    public static List<ShotItem> List()
    {
        var list = new List<ShotItem>();
        foreach (var dir in new[] { ShotsDir, VideosDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (Parse(f) is { } item) list.Add(item);
            }
            catch (Exception e)
            {
                Log.Error($"снимки: папка {dir}", e);
            }
        }
        return list.OrderByDescending(i => i.Time).ThenByDescending(i => i.Path, StringComparer.Ordinal).ToList();
    }

    /// Удалить насовсем всё своё старше трёх дней. Сколько удалили.
    public static int Cleanup()
    {
        var count = 0;
        var edge = DateTime.Now - Keep;
        foreach (var item in List().Where(i => i.Time < edge))
        {
            try { File.Delete(item.Path); count++; }
            catch (Exception e) { Log.Write($"снимки: не удалился {item.Name}: {e.Message}"); }
        }
        if (count > 0) Log.Write($"снимки: удалено старше 3 дней — {count}");
        return count;
    }
}
