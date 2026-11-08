namespace WindowsShell.Island.Tests;

public class ExclusiveOwnerTests
{
    // Judge(hwnd, exclusiveState, covers, zoomed, resizable)

    [Fact] public void GameIsFull()
    {
        var o = new ExclusiveOwner();
        Assert.True(o.Judge(1, true, true, false, false));
    }

    [Fact] public void OtherMonitorNotFull()
    {
        // E22: CS2 в эксклюзиве, окно Claude развёрнуто на другом мониторе — состояние 3 общее на сеанс.
        var o = new ExclusiveOwner();
        o.Judge(1, true, true, false, false);
        Assert.False(o.Judge(2, true, true, true, true));
        Assert.True(o.Judge(1, true, true, false, false));
    }

    [Fact] public void OwnerClearedWhenStateDrops()
    {
        var o = new ExclusiveOwner();
        o.Judge(1, true, true, false, false);
        Assert.False(o.Judge(2, false, true, true, true));
        // Новый эксклюзив — новый владелец.
        Assert.True(o.Judge(2, true, true, false, false));
        Assert.False(o.Judge(1, true, true, true, true));
    }

    [Fact] public void NoOwnerUntilCovering()
    {
        var o = new ExclusiveOwner();
        Assert.False(o.Judge(5, true, false, false, true));
        Assert.True(o.Judge(6, true, true, false, false));
        Assert.False(o.Judge(5, true, false, false, true));
    }

    [Fact] public void BorderlessStillFullWithoutExclusive()
    {
        var o = new ExclusiveOwner();
        Assert.True(o.Judge(3, false, true, false, false));
        Assert.False(o.Judge(4, false, true, true, true));
    }
}

public class ShellUiTests
{
    [Theory]
    [InlineData("Windows.UI.Core.CoreWindow", "explorer", true)]
    [InlineData("Shell_TrayWnd", "explorer", true)]
    [InlineData("Shell_SecondaryTrayWnd", "explorer", true)]
    [InlineData("Progman", "explorer", true)]
    [InlineData("WorkerW", "explorer", true)]
    [InlineData("XamlExplorerHostIslandWindow", "explorer", true)]
    [InlineData("ForegroundStaging", "explorer", true)]
    [InlineData("MultitaskingViewFrame", "explorer", true)]
    [InlineData("Windows.UI.Core.CoreWindow", "SearchHost", true)]
    [InlineData("Anything", "StartMenuExperienceHost", true)]
    [InlineData("Anything", "ShellExperienceHost", true)]
    [InlineData("CabinetWClass", "explorer", false)]
    [InlineData("UnrealWindow", "game", false)]
    [InlineData("Chrome_WidgetWin_1", "chrome", false)]
    public void Detects(string cls, string exe, bool shell) => Assert.Equal(shell, ShellUi.Is(cls, exe));
}
