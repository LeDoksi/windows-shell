namespace WindowsShell.Island.Core;

/// Магнитный край: нижняя кромка тела тянется «носиком» к курсору под островом.
/// Координаты — DIP относительно верхнего края монитора. Формулы — из принятого живого эскиза.
public sealed class Magnet
{
    static readonly SpringParams AmpSpring = new(230, 0.32);
    static readonly SpringParams WidthSpring = new(200, 0.7);
    static readonly SpringParams PosSpring = new(300, 0.75);

    Spring _a;
    Spring _s = new() { Value = 70, Target = 70 };
    Spring _x;
    bool _placed;

    /// Высота носика, DIP (не меньше 0).
    public double Amplitude => Math.Max(0, _a.Value);

    /// Движется ли ещё магнит (нужен ли следующий кадр).
    public bool Moving => !_a.Settled(0.05) || !_s.Settled(0.05) || !_x.Settled(0.05);

    /// cursorInside — курсор на этом мониторе (иначе магнит гаснет).
    public void Update(double dt, double cursorX, double cursorY, bool cursorInside,
        double bodyW, double bodyH, double centerX, bool rest, bool reduced)
    {
        var radius = rest ? 110.0 : 160.0;
        var maxA = rest ? 22.0 : 46.0;
        var gap = cursorY - bodyH;
        var active = !reduced && cursorInside && gap > 0 && gap < radius
            && Math.Abs(cursorX - centerX) < bodyW / 2 + 30;

        if (active)
        {
            var a = Math.Min(maxA * Math.Pow(1 - gap / radius, 1.5), gap * 0.82);
            var reach = Math.Max(0, bodyW / 2 - Math.Max(18, Math.Min(30, bodyH / 2)) - 24);
            _a.Target = a;
            _s.Target = 70 - 40 * Math.Min(1, a / 46);
            _x.Target = Math.Clamp(cursorX, centerX - reach, centerX + reach);
            if (!_placed) { _x.Value = _x.Target; _x.Velocity = 0; _placed = true; }
        }
        else
        {
            _a.Target = 0;
            _s.Target = 70;
        }

        if (reduced)
        {   // Без пружин: носика нет совсем.
            _a = default;
            _s = new Spring { Value = 70, Target = 70 };
            _placed = false;
            return;
        }
        _a.Step(dt, AmpSpring);
        _s.Step(dt, WidthSpring);
        _x.Step(dt, PosSpring);
        if (!active && Amplitude < 0.01 && _a.Settled(0.01)) _placed = false;
    }

    /// Насколько кромка опущена в точке x: узкий носик плюс широкое основание.
    public double Offset(double x)
    {
        var amp = Amplitude;
        if (amp <= 0) return 0;
        var s = Math.Max(8, _s.Value);
        var dx = x - _x.Value;
        var narrow = 0.42 * s;
        return amp * (0.58 * Math.Exp(-dx * dx / (2 * s * s)) + 0.42 * Math.Exp(-dx * dx / (2 * narrow * narrow)));
    }
}
