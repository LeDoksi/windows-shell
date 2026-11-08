namespace Island.Tests;

public class NoticeStoreTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    static Notice N(uint id, string app, int sec = 0) => new() { WinId = id, App = app, Aumid = app + ".id", Title = $"t{id}", Body = $"b{id}", Time = T0.AddSeconds(sec) };

    [Fact]
    public void AddIsUnreadAndNewestFirst()
    {
        var s = new NoticeStore();
        s.Add(N(1, "Telegram"), pill: true);
        s.Add(N(2, "Почта", 5), pill: true);
        Assert.True(s.Unread);
        Assert.Equal(2, s.UnreadCount);
        Assert.Equal(2u, s.Items[0].WinId);
    }

    [Fact]
    public void SameIdOnce()
    {
        var s = new NoticeStore();
        s.Add(N(1, "Telegram"), pill: true);
        var again = s.Add(N(1, "Telegram"), pill: true);
        Assert.False(again.Pill);
        Assert.Single(s.Items);
    }

    [Fact]
    public void BurstOnePill()
    {
        var s = new NoticeStore();
        NoticeAdd last = default;
        for (uint i = 1; i <= 5; i++) last = s.Add(N(i, "Telegram", (int)i), pill: true);
        Assert.True(last.Pill);
        Assert.Equal(5, last.Burst);
        Assert.Equal(5, s.Items.Count);
        var other = s.Add(N(9, "Почта", 9), pill: true);
        Assert.Equal(1, other.Burst);
        s.EndBurst();
        Assert.Equal(1, s.Add(N(10, "Почта", 10), pill: true).Burst);
    }

    [Fact]
    public void QuietAddsWithoutPill()
    {
        var s = new NoticeStore();
        var r = s.Add(N(1, "Telegram"), pill: false);
        Assert.False(r.Pill);
        Assert.True(s.Unread);
    }

    [Fact]
    public void ImportIsQuiet()
    {
        var s = new NoticeStore();
        s.Add(N(1, "Telegram"), pill: true);
        var n = s.Import([N(1, "Telegram"), N(2, "Почта", -60), N(3, "Старое", -4 * 24 * 3600)], T0);
        Assert.Equal(1, n);
        Assert.Equal(2, s.Items.Count);
        Assert.Contains(s.Items, i => i.WinId == 2 && !i.Read);
    }

    [Fact]
    public void GroupsByAppFreshestFirst()
    {
        var s = new NoticeStore();
        s.Add(N(1, "Почта", 0), true);
        s.Add(N(2, "Telegram", 10), true);
        s.Add(N(3, "Почта", 20), true);
        var g = s.Groups();
        Assert.Equal(["Почта", "Telegram"], g.Select(x => x.App));
        Assert.Equal([3u, 1u], g[0].Items.Select(i => i.WinId));
    }

    [Fact]
    public void ReadClearExpire()
    {
        var s = new NoticeStore();
        s.Add(N(1, "Почта", 0), true);
        s.Add(N(2, "Telegram", 10), true);
        s.MarkAllRead();
        Assert.False(s.Unread);
        s.Clear("Почта");
        Assert.Single(s.Items);
        Assert.True(s.Expire(T0.AddDays(3).AddSeconds(11)));
        Assert.Empty(s.Items);
        s.Add(N(3, "x"), true);
        s.ClearAll();
        Assert.Empty(s.Items);
    }

    [Fact]
    public void ClearedNotReimported()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notices-{Guid.NewGuid():N}.json");
        try
        {
            var s = new NoticeStore(path);
            s.Add(N(1, "Telegram"), true);
            s.ClearAll();
            s.Save();
            var t = new NoticeStore(path);
            t.Load();
            Assert.Equal(0, t.Import([N(1, "Telegram")], T0));
            Assert.Contains(1u, t.KnownIds());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notices-{Guid.NewGuid():N}.json");
        try
        {
            var s = new NoticeStore(path);
            s.Add(N(7, "Telegram"), true);
            s.Save();
            var t = new NoticeStore(path);
            t.Load();
            Assert.True(t.Known(7));
            Assert.Equal("b7", t.Items[0].Body);
        }
        finally { File.Delete(path); }
    }
}

public class NoticeRuleTests
{
    [Fact] public void PillWhenWindowsBannersOff() => Assert.True(NoticeRule.Pill(islandQuiet: false, windowsQuiet: true, eventOn: true));
    [Fact] public void NoPillWhileWindowsShowsBanners() => Assert.False(NoticeRule.Pill(false, false, true));
    [Fact] public void NoPillInIslandQuiet() => Assert.False(NoticeRule.Pill(true, true, true));
    [Fact] public void NoPillWhenEventOff() => Assert.False(NoticeRule.Pill(false, true, false));

    [Fact]
    public void NoticeRoutesLikeShot()
    {
        IReadOnlyList<string> screens = ["A", "B"];
        Assert.Equal("A", EventRoute.Target(EventKind.Notice, "A", new HashSet<string>(), null, screens));
        Assert.Equal("B", EventRoute.Target(EventKind.Notice, "A", new HashSet<string> { "A" }, null, screens));
        Assert.Null(EventRoute.Target(EventKind.Notice, "A", new HashSet<string> { "A" }, null, ["A"]));
    }
}

public class OcrLayoutTests
{
    [Fact] public void SmallTripled() => Assert.Equal(3, OcrLayout.Scale(400, 120, 10000));
    [Fact] public void MediumDoubled() => Assert.Equal(2, OcrLayout.Scale(1200, 300, 10000));
    [Fact] public void LargeKept() => Assert.Equal(1, OcrLayout.Scale(2560, 1440, 10000));
    [Fact] public void CappedByMax() => Assert.Equal(2000 / 1500.0, OcrLayout.Scale(1500, 100, 2000), 3);

    [Fact]
    public void JoinLinesAndParagraphs()
    {
        var t = OcrLayout.Join([("Вторая", 30, 20), ("Первая", 0, 20), ("Абзац", 100, 20), (" ", 140, 20)]);
        Assert.Equal("Первая\nВторая\n\nАбзац", t);
    }
}

public class TextFixTests
{
    [Fact] public void LayoutToRussian() => Assert.Equal("привет, как дела?", TextFix.Layout("ghbdtn? rfr ltkf&"));
    [Fact] public void LayoutToEnglish() => Assert.Equal("hello world", TextFix.Layout("руддщ цщкдв"));
    [Fact] public void LayoutRoundTrip() => Assert.Equal("Съешь же ещё этих мягких булок.", TextFix.Layout(TextFix.Layout("Съешь же ещё этих мягких булок.")));
    [Fact] public void LayoutKeepsDigits() => Assert.Equal("123 рус", TextFix.Layout("123 hec"));
    [Fact] public void Swap() => Assert.Equal("Привет, Мир", TextFix.SwapCase("пРИВЕТ, мИР"));
    [Fact] public void SentenceCase() => Assert.Equal("Это тест. Второе предложение! Да?", TextFix.Sentence("ЭТО ТЕСТ. второе ПРЕДЛОЖЕНИЕ! да?"));

    [Fact]
    public void JoinKeepsParagraphs()
    {
        Assert.Equal("Строка один и продолжение слова.\n\nНовый абзац.", TextFix.JoinLines("Строка один и про-\nдолжение  слова.\r\n\r\nНовый\nабзац.\n"));
    }

    [Fact] public void OneLineAll() => Assert.Equal("а б в", TextFix.OneLine("а\n\nб\r\n  в"));

    [Fact]
    public void OptionsOnlyChanging()
    {
        var o = TextFix.Options("hello");
        Assert.DoesNotContain(o, x => x.Id == "lower");
        Assert.Contains(o, x => x.Id == "upper" && x.Result == "HELLO");
        Assert.DoesNotContain(o, x => x.Id == "join");
    }

    [Theory]
    [InlineData("ghbdtn? rfr ltkf&", "layout")]
    [InlineData("руддщ цщкдв", "layout")]
    [InlineData("пРИВЕТ мИР", "swap")]
    [InlineData("hello world", null)]
    [InlineData("обычный русский текст", null)]
    [InlineData("строка\nещё строка", "join")]
    public void Suggests(string text, string? id) => Assert.Equal(id, TextFix.Suggest(text));
}
