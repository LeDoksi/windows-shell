namespace WindowsShell.Island.Tests;

public class SpringTests
{
    /// Гонит пружину от 0 к 1 одну секунду с частотой hz. settle — время, после которого
    /// |x − 1| ≤ 0,02 навсегда; overshoot — насколько значение ушло дальше цели.
    static (double settleMs, double overshoot) Run(SpringParams p, double hz)
    {
        var s = new Spring { Target = 1 };
        var dt = 1 / hz;
        var frames = (int)Math.Round(hz);
        double settle = 0, max = 0;
        for (var i = 1; i <= frames; i++)
        {
            s.Step(dt, p);
            if (Math.Abs(s.Value - 1) > 0.02) settle = i * dt;
            max = Math.Max(max, s.Value);
        }
        // Первый кадр, начиная с которого значение в допуске.
        return ((settle + dt) * 1000, max - 1);
    }

    [Theory] [InlineData(60)] [InlineData(144)] [InlineData(240)]
    public void Presets_MeetMotionBudget(double hz)
    {
        Assert.InRange(Run(Springs.Ribbon, hz).settleMs, 150, 210);
        Assert.InRange(Run(Springs.Switch, hz).settleMs, 200, 260);
        Assert.InRange(Run(Springs.Widget, hz).settleMs, 250, 320);
        foreach (var p in new[] { Springs.Ribbon, Springs.Switch, Springs.Widget })
            Assert.InRange(Run(p, hz).overshoot, 0.01, 0.04);   // лёгкий «долив», не желе
    }

    [Fact] public void FrameRateIndependent() =>
        Assert.InRange(Math.Abs(Run(Springs.Widget, 60).settleMs - Run(Springs.Widget, 144).settleMs), 0, 17);

    [Fact] public void Retarget_KeepsValueAndVelocity()
    {   // 10 шагов к 1, затем цель 0: значение и скорость не прыгают
        var s = new Spring { Target = 1 };
        for (var i = 0; i < 10; i++) s.Step(1 / 60.0, Springs.Widget);
        var (v, vel) = (s.Value, s.Velocity);
        s.Target = 0;
        s.Step(1e-6, Springs.Widget);
        Assert.Equal(v, s.Value, 3);
        Assert.Equal(vel, s.Velocity, 0);
    }

    [Fact] public void LargeDt_IsStable()
    {
        var s = new Spring { Target = 1 };
        s.Step(0.5, Springs.Ribbon);
        Assert.False(double.IsNaN(s.Value));
        Assert.InRange(s.Value, 0, 1.04);
    }

    [Fact] public void OverdampedIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpringParams(400, 1.2).Validate());

    [Fact] public void Settled_AfterLongRun()
    {
        var s = new Spring { Target = 1 };
        Assert.False(s.Settled(0.001));
        for (var i = 0; i < 120; i++) s.Step(1 / 60.0, Springs.Ribbon);
        Assert.True(s.Settled(0.001));
    }
}
