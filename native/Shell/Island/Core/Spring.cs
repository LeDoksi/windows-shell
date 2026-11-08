namespace WindowsShell.Island.Core;

/// Параметры пружины: жёсткость k (ω = √k) и относительное затухание ζ.
/// ζ строго между 0 и 1: лёгкий «долив» без желе; ζ ≥ 1 движение затягивает.
public readonly record struct SpringParams(double Stiffness, double Damping)
{
    public void Validate()
    {
        if (!(Stiffness > 0) || double.IsInfinity(Stiffness))
            throw new ArgumentOutOfRangeException(nameof(Stiffness), Stiffness, "жёсткость должна быть больше 0");
        if (!(Damping > 0 && Damping < 1))
            throw new ArgumentOutOfRangeException(nameof(Damping), Damping, "затухание должно быть строго между 0 и 1");
    }
}

/// Пружины острова из принципов motion: лента ≈200 мс, смена виджета ≈250 мс, виджет ≈300 мс.
public static class Springs
{
    public static readonly SpringParams Ribbon = new(900, 0.75);
    public static readonly SpringParams Switch = new(700, 0.75);
    public static readonly SpringParams Widget = new(420, 0.75);
    public static readonly SpringParams Highlight = new(520, 0.85);
}

/// Одна величина на пружине. Шаг — точное решение затухающего осциллятора, а не Эйлер,
/// поэтому результат не зависит от частоты кадров и не «выстреливает» на долгом кадре.
public struct Spring
{
    public double Value;
    public double Velocity;
    public double Target;

    public void Step(double dt, SpringParams p)
    {
        p.Validate();
        if (!(dt > 0)) return;
        var e = Value - Target;
        var w = Math.Sqrt(p.Stiffness);
        var a = p.Damping * w;
        var wd = w * Math.Sqrt(1 - p.Damping * p.Damping);
        var c1 = e;
        var c2 = (Velocity + a * e) / wd;
        var decay = Math.Exp(-a * dt);
        var cos = Math.Cos(wd * dt);
        var sin = Math.Sin(wd * dt);
        var e2 = decay * (c1 * cos + c2 * sin);
        // Производная того же выражения по времени.
        var v2 = decay * ((c2 * wd - a * c1) * cos - (c1 * wd + a * c2) * sin);
        Value = Target + e2;
        Velocity = v2;
    }

    public readonly bool Settled(double eps) =>
        Math.Abs(Value - Target) < eps && Math.Abs(Velocity) < eps * 10;
}
