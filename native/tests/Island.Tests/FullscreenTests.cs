namespace WindowsShell.Island.Tests;

public class FullscreenTests
{
    [Theory]
    [InlineData(true, false, false, true, true)]    // exclusive D3D
    [InlineData(false, true, false, false, true)]   // F11 в браузере: весь экран, без рамки
    [InlineData(false, true, true, true, false)]    // развёрнутое окно ChatGPT (WSH-100)
    [InlineData(false, false, false, true, false)]  // обычное окно
    public void Rule_MatchesBars(bool excl, bool covers, bool zoomed, bool resizable, bool full) =>
        Assert.Equal(full, FullscreenRule.IsFull(excl, covers, zoomed, resizable));

    [Fact] public void GameStaysFull_WhenFocusMovesToOtherScreen()
    {
        var m = new FullscreenMap();
        m.Observe("A", 1, true);
        m.Observe("B", 2, false);
        Assert.True(m.IsFull("A")); Assert.False(m.IsFull("B"));
    }

    [Fact] public void ClosedGame_IsPruned()
    {
        var m = new FullscreenMap();
        m.Observe("A", 1, true);
        m.Prune((h, s) => false);
        Assert.False(m.IsFull("A"));
    }

    [Fact] public void StillRunningGame_SurvivesPrune()
    {
        var m = new FullscreenMap();
        m.Observe("A", 1, true);
        m.Prune((h, s) => h == 1 && s == "A");
        Assert.True(m.IsFull("A"));
    }

    [Fact] public void TwoFullscreenApps_BothCount()
    {
        var m = new FullscreenMap();
        m.Observe("A", 1, true); m.Observe("B", 2, true);
        Assert.Equal(new[] { "A", "B" }, m.FullScreens.OrderBy(x => x));
    }

    [Fact] public void NormalWindowOnSameScreen_Clears()
    {
        var m = new FullscreenMap();
        m.Observe("A", 1, true); m.Observe("A", 3, false);
        Assert.False(m.IsFull("A"));
    }

    [Fact] public void GameMovedToOtherScreen_LeavesOldScreen()
    {
        var m = new FullscreenMap();
        m.Observe("A", 1, true); m.Observe("B", 1, true);
        Assert.False(m.IsFull("A")); Assert.True(m.IsFull("B"));
    }
}
