namespace WindowsShell.Island.Tests;

public class HoverTests
{
    // Step(now, atHot, insideBody, held, blocked, open)

    [Fact] public void WakesAfter150msAtEdge()
    {
        var g = new HoverGate();
        Assert.Equal(HoverStep.None, g.Step(1000, true, false, false, false, false));
        Assert.Equal(HoverStep.None, g.Step(1149, true, false, false, false, false));
        Assert.Equal(HoverStep.Wake, g.Step(1150, true, false, false, false, false));
    }

    [Fact] public void HeldButtonResetsTimer()
    {
        var g = new HoverGate();
        g.Step(0, true, false, false, false, false);
        Assert.Equal(HoverStep.None, g.Step(100, true, false, true, false, false));
        Assert.Equal(HoverStep.None, g.Step(200, true, false, false, false, false));
        Assert.Equal(HoverStep.Wake, g.Step(350, true, false, false, false, false));
    }

    [Fact] public void FullscreenBlocksWake()
    {
        var g = new HoverGate();
        g.Step(0, true, false, false, true, false);
        Assert.Equal(HoverStep.None, g.Step(500, true, false, false, true, false));
    }

    [Fact] public void LeavingCursorWaitsAway()
    {
        var g = new HoverGate();
        g.Step(0, true, false, false, false, false);
        g.Step(150, true, false, false, false, false);
        Assert.Equal(HoverStep.None, g.Step(200, false, false, false, false, false));
        Assert.Equal(HoverStep.None, g.Step(599, false, false, false, false, false));
        Assert.Equal(HoverStep.Sleep, g.Step(600, false, false, false, false, false));
    }

    [Fact] public void ComingBackCancelsSleep()
    {
        var g = new HoverGate();
        g.Step(0, true, false, false, false, false);
        g.Step(150, true, false, false, false, false);
        g.Step(200, false, false, false, false, false);
        g.Step(300, false, true, false, false, false);
        Assert.Equal(HoverStep.None, g.Step(700, false, false, false, false, false));
        Assert.Equal(HoverStep.Sleep, g.Step(1100, false, false, false, false, false));
    }

    [Fact] public void OpenWidgetNeverSleepsByLeaving()
    {
        var g = new HoverGate();
        g.Step(0, true, false, false, false, false);
        g.Step(150, true, false, false, false, false);
        g.Step(200, false, false, false, false, true);
        Assert.Equal(HoverStep.None, g.Step(5000, false, false, false, false, true));
    }
}

public class RibbonTests
{
    static readonly string[] Known = ["media", "charge", "net", "sound", "lang"];

    [Fact] public void KeepsSavedOrder_AppendsMissing_DropsJunk() =>
        Assert.Equal(["sound", "media", "charge", "net", "lang"], RibbonOrder.Normalize(["sound", "x", "sound"], Known));

    [Fact] public void NullMeansDefault() => Assert.Equal(Known, RibbonOrder.Normalize(null, Known));

    [Fact] public void Move() =>
        Assert.Equal(["charge", "media", "net", "sound", "lang"], RibbonOrder.Move(Known, "charge", -1));

    [Fact] public void MoveAtEdgeIsNoop() => Assert.Equal(Known, RibbonOrder.Move(Known, "media", -1));
}

public class EventRouteTests
{
    static readonly string[] Two = ["A", "B"];

    [Fact] public void LanguageOnActiveScreen() =>
        Assert.Equal("A", EventRoute.Target(EventKind.Language, "A", new HashSet<string>(), null, Two));

    [Fact] public void LanguageSuppressedInFullscreen() =>
        Assert.Null(EventRoute.Target(EventKind.Language, "A", new HashSet<string> { "A" }, null, Two));

    [Fact] public void ChargeOverBorderlessGame() =>
        Assert.Equal("A", EventRoute.Target(EventKind.Charge, "A", new HashSet<string> { "A" }, null, Two));

    [Fact] public void ChargeLeavesExclusiveScreen() =>
        Assert.Equal("B", EventRoute.Target(EventKind.Charge, "A", new HashSet<string> { "A" }, "A", Two));

    [Fact] public void ChargeStaysWhenNoOtherScreen() =>
        Assert.Equal("A", EventRoute.Target(EventKind.Charge, "A", new HashSet<string> { "A" }, "A", ["A"]));

    [Fact] public void ChargePrefersFreeScreen() =>
        Assert.Equal("C", EventRoute.Target(EventKind.Charge, "A", new HashSet<string> { "A", "B" }, "A", ["A", "B", "C"]));
}
