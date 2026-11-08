namespace WindowsShell.Island.Core;

/// Кольцо вокруг обложки (медиа, вариант B, П19): доля по углу, попадание в кольцо, время.
public static class MediaRing
{
    /// Доля круга от верхней точки по часовой стрелке, 0..1. dx, dy — от центра (y вниз).
    public static double Fraction(double dx, double dy)
    {
        var a = Math.Atan2(dx, -dy);
        if (a < 0) a += 2 * Math.PI;
        return a / (2 * Math.PI);
    }

    public static bool OnRing(double dx, double dy, double rInner, double rOuter)
    {
        var d = Math.Sqrt(dx * dx + dy * dy);
        return d >= rInner && d <= rOuter;
    }

    /// Перемотка честная, только если плеер её разрешает и длительность известна.
    public static bool Seekable(bool canSeek, TimeSpan duration) => canSeek && duration.TotalSeconds > 1;

    public static string Time(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
