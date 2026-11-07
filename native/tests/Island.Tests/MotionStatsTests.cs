namespace WindowsShell.Island.Tests;

public class MotionStatsTests
{
    [Theory]
    [InlineData(0, 1000 / 60.0)]   // неизвестно
    [InlineData(1, 1000 / 60.0)]   // «по умолчанию железа» — тоже неизвестно, не 1 Гц
    [InlineData(144, 1000 / 144.0)]
    [InlineData(60, 1000 / 60.0)]
    public void PeriodMs_TreatsZeroAndOneAsUnknown(int hz, double want) =>
        Assert.Equal(want, MotionStats.PeriodMs(hz), 6);

    [Fact] public void SettleClock_UsesTwoPercentBand()
    {   // 62 → 262: допуск 2 % = 4 DIP. В 100 мс ещё 250 (вне), в 120 мс 259 (внутри), дальше внутри.
        var c = new SettleClock(190, 62, 560, 262);
        c.Sample(100, 560, 250);
        c.Sample(120, 560, 259);
        c.Sample(140, 560, 262.5);
        Assert.Equal(120, c.SettledMs);
    }

    [Fact] public void SettleClock_OvershootOutOfBandResets()
    {
        var c = new SettleClock(190, 3, 560, 62);
        c.Sample(100, 560, 61);    // внутри (допуск 1,18)
        c.Sample(120, 560, 64);    // «долив» вышел из допуска
        c.Sample(150, 560, 62.2);  // вернулся
        Assert.Equal(150, c.SettledMs);
    }

    [Fact] public void SettleClock_NeverInside_IsNull()
    {
        var c = new SettleClock(190, 3, 560, 62);
        c.Sample(10, 300, 30);
        Assert.Null(c.SettledMs);
    }

    [Fact] public void LatencyInPeriods()
    {
        Assert.Equal(1.5, MotionStats.InPeriods(25, 1000 / 60.0), 3);
    }

    [Fact] public void Percentile_Nearest()
    {
        var xs = new List<double> { 5, 1, 4, 2, 3 };
        Assert.Equal(3, MotionStats.Percentile(xs, 0.5));
        Assert.Equal(5, MotionStats.Percentile(xs, 0.95));
        Assert.Equal(0, MotionStats.Percentile([], 0.5));
    }
}
