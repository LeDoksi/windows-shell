namespace WindowsShell.Island.Tests;

public class GameRuleTests
{
    [Theory]
    [InlineData(true, true, false, false, false, GameReason.Marked)]
    [InlineData(false, false, true, true, true, GameReason.MarkedNot)]
    [InlineData(null, false, true, false, false, GameReason.Launcher)]
    [InlineData(null, false, false, true, true, GameReason.FullscreenFrames)]  // D:\Games\…, Battle.net, VK Play
    [InlineData(null, false, false, true, false, GameReason.None)]             // весь экран, но кадров нет (презентация)
    [InlineData(null, false, false, false, true, GameReason.None)]             // рисует, но в окне — не игра автоматически
    public void Decide(bool? marked, bool never, bool launcher, bool full, bool frames, GameReason want) =>
        Assert.Equal(want, GameRule.Decide(marked, never, launcher, full, frames));

    [Fact] public void Browser_FullscreenFrames_IsNotGame() =>
        Assert.Equal(GameReason.Never, GameRule.Decide(null, inNever: true, false, true, true));

    [Theory]
    [InlineData(GameReason.Marked, true)]
    [InlineData(GameReason.Launcher, true)]
    [InlineData(GameReason.FullscreenFrames, true)]
    [InlineData(GameReason.MarkedNot, false)]
    [InlineData(GameReason.Never, false)]
    [InlineData(GameReason.None, false)]
    public void IsGame(GameReason r, bool want) => Assert.Equal(want, GameRule.IsGame(r));
}
