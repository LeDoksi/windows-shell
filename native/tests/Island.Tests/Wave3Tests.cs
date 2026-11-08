namespace WindowsShell.Island.Tests;

public class PinTests
{
    static readonly string[] Three = ["D1", "D2", "D3"];

    [Fact] public void NotPinned() => Assert.Equal((null, false), PinPlace.Where(null, Three));
    [Fact] public void OnItsScreen() => Assert.Equal(("D2", false), PinPlace.Where("D2", Three));
    [Fact] public void SleepsWhenScreenGone() => Assert.Equal((null, true), PinPlace.Where("D2", ["D1", "D3"]));

    [Fact] public void ComesBack()
    {
        Assert.True(PinPlace.Where("D2", ["D1"]).Sleeping);
        Assert.Equal(("D2", false), PinPlace.Where("D2", ["D1", "D2"]));
    }
}

public class FrameStatsTests
{
    static FrameStats Run(int frames, double ms, double start = 0)
    {
        var s = new FrameStats();
        for (var i = 0; i < frames; i++) s.Add(start + (i + 1) * ms, ms);
        return s;
    }

    [Fact] public void FpsFromShown()
    {
        var s = Run(100, 10);
        Assert.Equal(100, s.Fps(1000)!.Value, 1);
        Assert.Equal(10, s.FrameMs(1000)!.Value, 1);
    }

    [Fact] public void NullWhenStale() => Assert.Null(Run(100, 10).Fps(1000 + 1600));

    [Fact] public void Low1NeedsHundred()
    {
        Assert.Null(Run(99, 10).Low1);
        Assert.NotNull(Run(100, 10).Low1);
    }

    [Fact] public void Low1WorstPercent()
    {
        var s = new FrameStats();
        double t = 0;
        for (var i = 0; i < 198; i++) { t += 10; s.Add(t, 10); }
        t += 50; s.Add(t, 50);
        t += 50; s.Add(t, 50);
        // Худший 1 % из 200 — два кадра по 50 мс = 20 FPS.
        Assert.Equal(20, s.Low1!.Value, 1);
    }

    [Fact] public void DisplayedNaFallsBackToPresented()
    {
        var s = new FrameStats();
        for (var i = 1; i <= 60; i++) s.AddPresented(i * 16.0, 16);
        Assert.True(s.PresentedOnly);
        Assert.Equal(62.5, s.Fps(60 * 16.0)!.Value, 1);
    }

    [Fact] public void ShownWinsOverPresented()
    {
        var s = new FrameStats();
        for (var i = 1; i <= 10; i++) s.AddPresented(i * 5.0, 5);
        s.Add(60, 10);
        s.AddPresented(65, 5);
        Assert.Equal(1, s.Count);
        Assert.False(s.PresentedOnly);
    }

    [Fact] public void RecentForGraph()
    {
        var s = Run(50, 10);
        Assert.Equal(40, s.Recent(40).Length);
        Assert.Equal(3, Run(3, 10).Recent(40).Length);
    }

    [Fact] public void PickColumns2x()
    {
        var c = FrameColumns.Pick("Application,ProcessID,SwapChainAddress,PresentRuntime,CPUStartQPCTime,FrameTime,CPUBusy,DisplayLatency,DisplayedTime".Split(','));
        Assert.Equal(1, c.Pid);
        Assert.Equal(4, c.Time);
        Assert.True(c.QpcTime);
        Assert.Equal(5, c.Presented);
        Assert.Equal(7, c.Latency);
        Assert.Equal(8, c.Shown);
        Assert.Equal(-1, c.Dropped);
    }

    [Fact] public void PickColumns1x()
    {
        var c = FrameColumns.Pick("Application,ProcessID,TimeInSeconds,MsBetweenPresents,MsBetweenDisplayChange,Dropped,MsUntilDisplayed".Split(','));
        Assert.Equal(3, c.Presented);
        Assert.Equal(4, c.Shown);
        Assert.Equal(5, c.Dropped);
        Assert.False(c.QpcTime);
    }
}

public class GameProbeTests
{
    [Fact] public void FramesMakeGame()
    {
        var p = new GameProbe(4000);
        Assert.Equal(new ProbeDecision(42, null), p.Step(0, 42, true, true, false));
        Assert.Equal(new ProbeDecision(42, true), p.Step(1500, 42, true, true, true));
        Assert.True(p.Known(42));
    }

    [Fact] public void NoFramesNotGame()
    {
        var p = new GameProbe(4000);
        p.Step(0, 42, true, true, false);
        Assert.Equal(new ProbeDecision(42, null), p.Step(3000, 42, true, true, false));
        Assert.Equal(new ProbeDecision(0, false), p.Step(4000, 42, true, true, false));
        Assert.False(p.Known(42));
    }

    [Fact] public void LeftFullscreenUndecided()
    {
        var p = new GameProbe(4000);
        p.Step(0, 42, true, true, false);
        Assert.Equal(new ProbeDecision(0, null), p.Step(1000, 42, false, true, false));
        Assert.Null(p.Known(42));
        // Вернулся на весь экран — проба с начала.
        p.Step(2000, 42, true, true, false);
        Assert.Null(p.Step(5000, 42, true, true, false).Game);
    }

    [Fact] public void RememberedPidAnswersAtOnce()
    {
        var p = new GameProbe(4000);
        p.Step(0, 42, true, true, false);
        p.Step(100, 42, true, true, true);
        // Игра вышла из fullscreen (меню в окне) — всё равно игра.
        Assert.Equal(new ProbeDecision(42, true), p.Step(9000, 42, false, true, false));
        p.Forget(42);
        Assert.Null(p.Known(42));
    }

    [Fact] public void NotCandidateNotTracked() => Assert.Equal(new ProbeDecision(0, null), new GameProbe().Step(0, 42, true, false, false));
}

public class BoardTests
{
    static KItem I(string id, string status, string? epic = null, DateTimeOffset? closed = null, bool archived = false, string project = "p", string title = "") => new()
    {
        Id = id, Status = status, EpicId = epic, ClosedAt = closed, ArchivedAt = archived ? DateTimeOffset.UnixEpoch : null, ProjectId = project, Title = title,
    };

    [Fact] public void ColumnsLikeSite()
    {
        Assert.Equal(["hold", "backlog", "doing", "waiting", "done"], Board.Columns);
        Assert.Equal(["Hold", "Backlog", "В работе", "Нужно от тебя", "Готово"], Board.Columns.Select(Board.StatusName));
        Assert.Equal("Нужно от тебя", KStatus.Name("waiting"));
    }

    [Fact] public void TypeNames()
    {
        Assert.Null(Board.TypeName("task"));
        Assert.Equal("баг", Board.TypeName("bug"));
        Assert.Equal("долг", Board.TypeName("chore"));
    }

    [Fact] public void RecentDoneFive()
    {
        var t = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var items = Enumerable.Range(1, 8).Select(n => I($"W-{n}", "done", closed: t.AddDays(n))).ToList();
        items.Add(I("W-99", "done", closed: t.AddDays(99), archived: true));
        Assert.Equal(["W-8", "W-7", "W-6", "W-5", "W-4"], Board.RecentDone(items, "p").Select(i => i.Id));
    }

    [Fact] public void ArchiveSplitsArchivedAndOverflow()
    {
        var t = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        var items = Enumerable.Range(1, 7).Select(n => I($"W-{n}", "done", closed: t.AddDays(n))).ToList();
        items.Add(I("W-50", "backlog", archived: true));
        items.Add(I("X-1", "done", closed: t, project: "other"));
        var (archived, overflow) = Board.Archive(items, "p", "");
        Assert.Equal(["W-50"], archived.Select(i => i.Id));
        Assert.Equal(["W-2", "W-1"], overflow.Select(i => i.Id));
    }

    [Fact] public void SearchIgnoresCaseAndYo()
    {
        var item = I("WSH-12", "done", title: "Ёлка на панели");
        Assert.True(Board.Matches(item, "елка"));
        Assert.True(Board.Matches(item, "ПАНЕЛ"));
        Assert.True(Board.Matches(item, "wsh-12"));
        Assert.True(Board.Matches(item, "  "));
        Assert.False(Board.Matches(item, "док"));
    }

    [Fact] public void GroupsByEpicPositionNoEpicLast()
    {
        KEpic[] epics = [new() { Id = "P-E2", Seq = 2, Position = 100 }, new() { Id = "P-E1", Seq = 1, Position = 200 }];
        var g = Board.Groups([I("a", "doing"), I("b", "doing", "P-E1"), I("c", "doing", "P-E2"), I("d", "doing", "P-E9")], epics);
        Assert.Equal(["P-E2", "P-E1", null], g.Select(x => x.Epic?.Id));
        Assert.Equal(["a", "d"], g[2].Items.Select(i => i.Id));
    }

    [Fact] public void EpicColorStable()
    {
        Assert.Equal(Board.EpicColor("WSH-E22"), Board.EpicColor("WSH-E22"));
        Assert.NotEqual(Board.EpicColor("WSH-E22"), Board.EpicColor("WSH-E23"));
        Assert.Equal(0xFF6E6E76u, Board.EpicColor(null));
    }

    [Fact] public void WaitingCountSkipsArchived()
    {
        KProject[] projects = [new() { Id = "p" }];
        var items = new[] { I("1", "waiting"), I("2", "waiting", archived: true), I("3", "doing"), I("4", "waiting", project: "gone") };
        Assert.Equal(1, Board.WaitingCount(items, projects));
    }

    [Fact] public void UndoKeepsClosedAt()
    {
        var closed = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var item = I("1", "done", closed: closed);
        item.Position = 350;
        Assert.Equal(("done", 350.0, (DateTimeOffset?)closed), Board.Undo(item));
    }
}
