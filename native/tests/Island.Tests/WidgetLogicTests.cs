namespace WindowsShell.Island.Tests;

public class WifiTests
{
    [Theory]
    [InlineData(1, WifiSecurity.Open)]
    [InlineData(10, WifiSecurity.Open)]
    [InlineData(4, WifiSecurity.Personal)]
    [InlineData(7, WifiSecurity.Personal)]
    [InlineData(8, WifiSecurity.Wpa3Personal)]
    [InlineData(9, WifiSecurity.Wpa3Personal)]
    [InlineData(3, WifiSecurity.Enterprise)]
    [InlineData(6, WifiSecurity.Enterprise)]
    [InlineData(11, WifiSecurity.Enterprise)]
    [InlineData(42, WifiSecurity.Enterprise)]
    public void Auth(int algo, WifiSecurity s) => Assert.Equal(s, WifiAuth.Of(algo));

    [Fact] public void Wpa2Xml()
    {
        var x = WifiProfile.Xml("Дом", WifiSecurity.Personal, "secret123");
        Assert.Contains("<authentication>WPA2PSK</authentication>", x);
        Assert.Contains("<encryption>AES</encryption>", x);
        Assert.Contains("<keyMaterial>secret123</keyMaterial>", x);
        Assert.Contains("<hex>D094D0BED0BC</hex>", x);
        Assert.Contains("<name>Дом</name>", x);
    }

    [Fact] public void Wpa3Xml() => Assert.Contains("<authentication>WPA3SAE</authentication>", WifiProfile.Xml("n", WifiSecurity.Wpa3Personal, "12345678"));

    [Fact] public void OpenXml()
    {
        var x = WifiProfile.Xml("Кафе", WifiSecurity.Open, null);
        Assert.Contains("<authentication>open</authentication>", x);
        Assert.Contains("<encryption>none</encryption>", x);
        Assert.DoesNotContain("sharedKey", x);
    }

    [Fact] public void Escapes()
    {
        var x = WifiProfile.Xml("A&B<1>", WifiSecurity.Personal, "p&<>\"'12");
        Assert.Contains("<name>A&amp;B&lt;1&gt;</name>", x);
        Assert.Contains("<keyMaterial>p&amp;&lt;&gt;&quot;&apos;12</keyMaterial>", x);
    }

    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("123456789012345678901234567890123456789012345678901234567890123", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg", false)]
    [InlineData(null, false)]
    public void Password(string? p, bool ok) => Assert.Equal(ok, WifiProfile.PasswordOk(WifiSecurity.Personal, p));

    [Fact] public void OpenNeedsNoPassword() => Assert.True(WifiProfile.PasswordOk(WifiSecurity.Open, null));

    [Fact] public void EnterpriseNotHere() => Assert.False(WifiProfile.PasswordOk(WifiSecurity.Enterprise, "12345678"));
}

public class MediaRingTests
{
    [Theory]
    [InlineData(0, -1, 0)]
    [InlineData(1, 0, 0.25)]
    [InlineData(0, 1, 0.5)]
    [InlineData(-1, 0, 0.75)]
    public void Fraction(double dx, double dy, double f) => Assert.Equal(f, MediaRing.Fraction(dx, dy), 6);

    [Fact] public void OnRing()
    {
        Assert.True(MediaRing.OnRing(0, -80, 72, 92));
        Assert.False(MediaRing.OnRing(0, -40, 72, 92));
        Assert.False(MediaRing.OnRing(0, -100, 72, 92));
    }

    [Fact] public void NoDurationNotSeekable()
    {
        Assert.False(MediaRing.Seekable(true, TimeSpan.Zero));
        Assert.False(MediaRing.Seekable(false, TimeSpan.FromMinutes(3)));
        Assert.True(MediaRing.Seekable(true, TimeSpan.FromMinutes(3)));
    }

    [Fact] public void Time()
    {
        Assert.Equal("0:05", MediaRing.Time(TimeSpan.FromSeconds(5)));
        Assert.Equal("1:12", MediaRing.Time(TimeSpan.FromSeconds(72)));
        Assert.Equal("1:02:03", MediaRing.Time(TimeSpan.FromSeconds(3723)));
        Assert.Equal("0:00", MediaRing.Time(TimeSpan.FromSeconds(-3)));
    }
}
