namespace WindowsShell.Island.Core;

/// Счёт для отчёта пробы: период монитора, задержка в периодах, процентили.
public static class MotionStats
{
    /// Период кадра в мс. 0 и 1 Гц Windows отдаёт как «по умолчанию железа» — считаем 60 Гц.
    public static double PeriodMs(int hz) => 1000.0 / (hz > 1 ? hz : 60);

    public static double InPeriods(double ms, double periodMs) => ms / periodMs;

    /// Ближайший ранг; пустой список — 0.
    public static double Percentile(IReadOnlyList<double> xs, double q)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s[(int)Math.Clamp(Math.Round(q * (s.Count - 1)), 0, s.Count - 1)];
    }
}

/// Когда форма «пришла»: первый кадр, после которого ширина и высота остаются в пределах
/// 2 % пути до цели (как бюджет motion в тестах пружин), а не до 0,3 DIP.
public sealed class SettleClock(double fromW, double fromH, double toW, double toH)
{
    readonly double _bandW = Math.Max(0.5, 0.02 * Math.Abs(toW - fromW));
    readonly double _bandH = Math.Max(0.5, 0.02 * Math.Abs(toH - fromH));

    public double? SettledMs { get; private set; }

    public void Sample(double tMs, double w, double h)
    {
        var inside = Math.Abs(w - toW) <= _bandW && Math.Abs(h - toH) <= _bandH;
        if (!inside) SettledMs = null;
        else SettledMs ??= tMs;
    }
}
