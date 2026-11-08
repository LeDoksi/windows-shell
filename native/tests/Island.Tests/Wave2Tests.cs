namespace WindowsShell.Island.Tests;

public class SnapKeyTests
{
    [Fact] public void PressFires() => Assert.True(new SnapKey().Step(true, 1000));

    [Fact] public void RepeatDoesNot()
    {
        var k = new SnapKey();
        k.Step(true, 1000);
        Assert.False(k.Step(true, 1500));
        Assert.False(k.Step(true, 1530));
    }

    [Fact] public void ReleaseThenPress()
    {
        var k = new SnapKey();
        k.Step(true, 1000);
        Assert.False(k.Step(false, 1100));
        Assert.True(k.Step(true, 1300));
    }

    [Fact] public void LostReleaseDoesNotEatNextPress()
    {
        var k = new SnapKey();
        k.Step(true, 1000);
        // Отпускание не дошло (Win+L), через 2 с — новое нажатие.
        Assert.True(k.Step(true, 3000));
    }

    [Fact] public void UpOnlyFires()
    {
        var k = new SnapKey();
        Assert.True(k.Step(false, 1000));
        Assert.True(k.Step(false, 5000));
    }

    [Fact] public void TimerWrap()
    {
        var k = new SnapKey();
        k.Step(true, uint.MaxValue - 10);
        Assert.False(k.Step(true, 20));
        Assert.True(k.Step(true, 2000));
    }
}

public class RecordRouteTests
{
    static readonly string[] Three = ["A", "B", "C"];
    static HashSet<string> Full(params string[] s) => [.. s];

    [Fact] public void RecordedScreenWhenNoExclusive() =>
        Assert.Equal("A", new RecordRoute().Step(0, "A", null, Full(), Three));

    [Fact] public void BorderlessGameStaysOnRecorded() =>
        Assert.Equal("A", new RecordRoute().Step(0, "A", null, Full("A"), Three));

    [Fact] public void NeighbourWhenExclusive() =>
        Assert.Equal("C", new RecordRoute().Step(0, "A", "A", Full("A", "B"), Three));

    [Fact] public void ShortGapStays()
    {
        var r = new RecordRoute();
        Assert.Equal("B", r.Step(0, "A", "A", Full("A"), Three));
        Assert.Equal("B", r.Step(1000, "A", null, Full(), Three));
        Assert.Equal("B", r.Step(3000, "A", null, Full(), Three));
        Assert.Equal("B", r.Step(3500, "A", "A", Full("A"), Three));
        Assert.True(r.Away);
    }

    [Fact] public void BackAfterFiveSeconds()
    {
        var r = new RecordRoute();
        r.Step(0, "A", "A", Full("A"), Three);
        r.Step(1000, "A", null, Full(), Three);
        Assert.Equal("A", r.Step(6100, "A", null, Full(), Three));
        Assert.False(r.Away);
    }

    [Fact] public void OnlyScreenExclusive() =>
        Assert.Equal("A", new RecordRoute().Step(0, "A", "A", Full("A"), ["A"]));

    [Fact] public void NeighbourUnpluggedMoves()
    {
        var r = new RecordRoute();
        Assert.Equal("B", r.Step(0, "A", "A", Full("A"), Three));
        Assert.Equal("C", r.Step(10, "A", "A", Full("A"), ["A", "C"]));
    }

    [Theory]
    [InlineData(-1920, 0, Arrow.Left)]
    [InlineData(1920, 0, Arrow.Right)]
    [InlineData(0, -1080, Arrow.Up)]
    [InlineData(0, 1080, Arrow.Down)]
    public void Direction(int dx, int dy, Arrow a) =>
        Assert.Equal(a, RecordRoute.Direction(new RectI(0, 0, 1920, 1080), new RectI(dx, dy, dx + 1920, dy + 1080)));
}

public class ShotRouteTests
{
    static readonly string[] Two = ["A", "B"];

    [Fact] public void Active() => Assert.Equal("A", EventRoute.Target(EventKind.Shot, "A", new HashSet<string>(), null, Two));
    [Fact] public void GameGoesToOther() => Assert.Equal("B", EventRoute.Target(EventKind.Shot, "A", new HashSet<string> { "A" }, null, Two));
    [Fact] public void NoFreeScreenQuiet() => Assert.Null(EventRoute.Target(EventKind.Shot, "A", new HashSet<string> { "A", "B" }, "A", Two));
}

public class ShelfTests
{
    [Fact] public void NewOnTopNoDuplicates()
    {
        var l = new List<string> { @"C:\a.txt" };
        Assert.Equal(1, ShelfList.Add(l, [@"C:\b.txt", @"c:\A.TXT"]));
        Assert.Equal([@"C:\b.txt", @"c:\A.TXT"], l);
    }

    [Fact] public void RemoveIgnoresCase()
    {
        var l = new List<string> { @"C:\a.txt" };
        Assert.True(ShelfList.Remove(l, @"c:\A.txt"));
        Assert.Empty(l);
        Assert.False(ShelfList.Remove(l, "x"));
    }
}
