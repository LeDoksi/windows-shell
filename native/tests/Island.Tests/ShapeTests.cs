namespace WindowsShell.Island.Tests;

public class ShapeTests
{
    [Fact] public void Outline_SymmetricWithoutMagnet()
    {
        var pts = IslandShape.Outline(560, 62, 0, new Magnet());
        Assert.Equal(-280, pts.Min(p => p.X), 6);
        Assert.Equal(280, pts.Max(p => p.X), 6);
        Assert.Equal(62, pts.Max(p => p.Y), 6);
        Assert.Equal(-2, pts.Min(p => p.Y), 6);
    }

    [Fact] public void Outline_RejectsTooSmallBody()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IslandShape.Outline(30, 62, 0, new Magnet()));
        Assert.Throws<ArgumentOutOfRangeException>(() => IslandShape.Outline(190, 1, 0, new Magnet()));
    }

    [Fact] public void Magnet_PullsTipTowardCursor()
    {
        var m = new Magnet();
        for (var i = 0; i < 120; i++) m.Update(1 / 60.0, cursorX: 100, cursorY: 62 + 40, cursorInside: true, 560, 62, 0, rest: false, reduced: false);
        Assert.InRange(m.Amplitude, 10, 46);
        Assert.True(m.Offset(100) > m.Offset(0));        // носик под курсором
        Assert.True(m.Offset(100) > 3 * m.Offset(220));   // узкий носик, широкое основание гаснет
    }

    [Fact] public void Magnet_SilentOutsideRadiusAndReduced()
    {
        var far = new Magnet(); var red = new Magnet();
        for (var i = 0; i < 120; i++)
        {
            far.Update(1 / 60.0, 0, 62 + 200, true, 560, 62, 0, false, false);
            red.Update(1 / 60.0, 0, 62 + 40, true, 560, 62, 0, false, reduced: true);
        }
        Assert.Equal(0, far.Amplitude, 3);
        Assert.Equal(0, red.Amplitude, 3);
    }

    [Fact] public void Place_NegativeAndScaledMonitor()
    {
        var r = IslandLayout.Place(new RectI(-2560, -300, 0, 1140), 1.5, 560, 62);
        Assert.Equal(new RectI(-1700, -300, -860, -207), r);
    }
}
