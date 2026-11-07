namespace WindowsShell.Island.Core;

/// Контур тела острова: замкнутый многоугольник по часовой стрелке. Верх уходит на 2 DIP
/// за край экрана (без щели), нижние углы скруглены, нижняя кромка следует магниту.
public static class IslandShape
{
    const int CornerSegments = 6;

    public static IReadOnlyList<PointD> Outline(double w, double h, double centerX, Magnet magnet, int rimSamples = 64)
    {
        if (!(w >= 40)) throw new ArgumentOutOfRangeException(nameof(w), w, "ширина тела не меньше 40");
        if (!(h >= 2)) throw new ArgumentOutOfRangeException(nameof(h), h, "высота тела не меньше 2");
        if (rimSamples < 2) throw new ArgumentOutOfRangeException(nameof(rimSamples), rimSamples, null);

        var r = Math.Min(28, Math.Min(h / 2, w / 4));
        var left = centerX - w / 2;
        var right = centerX + w / 2;
        var pts = new List<PointD>(4 + 2 * (CornerSegments + 1) + rimSamples)
        {
            new(left, -2),
            new(right, -2),
            new(right, h - r),
        };

        // Правый нижний угол: от бока (0°) к низу (90°). Магнит подмешиваем к низу угла,
        // чтобы кромка не рвалась на стыке с краем.
        for (var i = 1; i <= CornerSegments; i++)
        {
            var t = i / (double)CornerSegments;
            var ang = t * Math.PI / 2;
            var x = right - r + r * Math.Cos(ang);
            pts.Add(new(x, h - r + r * Math.Sin(ang) + magnet.Offset(x) * Math.Sin(ang)));
        }

        // Нижняя кромка между углами (концы — уже точки углов).
        var from = right - r;
        var to = left + r;
        for (var i = 1; i < rimSamples - 1; i++)
        {
            var x = from + (to - from) * i / (rimSamples - 1);
            pts.Add(new(x, h + magnet.Offset(x)));
        }

        // Левый нижний угол: от низа (90°) к боку (180°).
        for (var i = 0; i <= CornerSegments; i++)
        {
            var ang = Math.PI / 2 + i / (double)CornerSegments * Math.PI / 2;
            var x = left + r + r * Math.Cos(ang);
            var sin = Math.Sin(ang);
            pts.Add(new(x, h - r + r * sin + magnet.Offset(x) * sin));
        }
        return pts;
    }
}
