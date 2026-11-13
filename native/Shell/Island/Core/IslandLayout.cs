namespace WindowsShell.Island.Core;

public readonly record struct PointD(double X, double Y);

/// Прямоугольник в физических пикселях экрана (как RECT в Win32).
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// Где стоит остров на мониторе: по центру верхнего края, в пикселях этого монитора.
public static class IslandLayout
{
    public static RectI Place(RectI monitor, double scale, double wDip, double hDip)
    {
        var w = (int)Math.Round(wDip * scale);
        var h = (int)Math.Round(hDip * scale);
        // Центр считаем в long: у мониторов слева координаты отрицательные.
        var center = (int)(((long)monitor.Left + monitor.Right) / 2);
        var left = center - w / 2;
        return new RectI(left, monitor.Top, left + w, monitor.Top + h);
    }
}
