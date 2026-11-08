using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Island.Core;
using WindowsShell.Perf;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island.Widgets;

/// «Монитор ПК» в острове (эскиз Pinned, П8, П16): ЦП, ГП, память, видеопамять с полосками за
/// последние секунды; когда идёт игра — FPS, 1% low, время кадра и почему это игра; кнопка
/// закрепления на этом экране. Цифры обновляются на месте раз в секунду, пересборка — только
/// когда меняется состав (появилась игра, закрепили, нет разрешения).
public sealed class PerfWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    static readonly Color PinBg = Color.FromArgb(255, 0x1F, 0x3A, 0x4A);
    static readonly Color Accent = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    const int Bars = 12;

    sealed class Card
    {
        public required TextBlock Value, Sub;
        public required StackPanel Spark;
    }

    readonly Dictionary<string, Card> _cards = [];
    string _shape = "";
    long _lastPaint;

    public override string Id => "perf";
    public override double Width => 600;

    protected override void OnOpen()
    {
        S.Perf.Hold(this, true);
        S.Perf.Updated += OnUpdated;
        S.Config.Changed += Later;
    }

    protected override void OnClose()
    {
        S.Perf.Updated -= OnUpdated;
        S.Config.Changed -= Later;
        S.Perf.Hold(this, false);
    }

    void OnUpdated()
    {
        if (!IsOpen) return;
        if (Shape() != _shape) { Rebuild(); return; }
        var now = Environment.TickCount64;
        if (now - _lastPaint < 900) return;
        _lastPaint = now;
        Paint();
    }

    /// Всё, от чего зависит состав виджета.
    string Shape()
    {
        var p = S.Perf;
        return $"{p.Game?.Exe}|{p.FpsState}|{p.LastApp?.Exe}|{p.Settings.PinScreen}|{PerfAccess.Current}|{S.Monitors.All.Count}";
    }

    protected override void Build()
    {
        _shape = Shape();
        _cards.Clear();
        var perf = S.Perf;

        var head = new Grid { Margin = new Thickness(4, 0, 0, 0), ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text("Монитор ПК", 18, bold: true));
        var pin = PinControl();
        Grid.SetColumn(pin, 1);
        head.Children.Add(pin);
        Root.Children.Add(head);

        if (perf.Game is { } game) Root.Children.Add(FpsBlock(game));

        var grid = new Grid { ColumnSpacing = 8 };
        string[] ids = ["cpu", "gpu", "ram", "vram"];
        for (var i = 0; i < ids.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = MakeCard(ids[i] switch { "cpu" => "ЦП", "gpu" => "ГП", "ram" => "ОЗУ", _ => "Видеопамять" }, out var c);
            _cards[ids[i]] = c;
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }
        Root.Children.Add(grid);

        if (perf.Game is null && perf.LastApp is { } app)
        {
            // Программа впереди не считается игрой — можно отметить (И8: ручная отметка главнее всего).
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Text($"{app.Name} — не игра: FPS не считается", 13, Sub));
            var mark = Btn("Это игра", click: () => perf.Mark(app.Exe, true));
            Grid.SetColumn(mark, 1);
            row.Children.Add(mark);
            Root.Children.Add(Box(row));
        }

        Root.Children.Add(new Border
        {
            Padding = new Thickness(4, 0, 4, 0),
            Child = Text("Какие показатели в строке закрепления и в игровом оверлее — в настройках (⚙ → Монитор ПК).", 12, Faint, wrap: true),
        });
        Paint();
    }

    Border Box(UIElement child) => new()
    {
        Background = B(Group),
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(12, 10, 12, 10),
        Child = child,
    };

    /// Скрепка (П8): закрепить здесь / закреплён здесь / закреплён за другим (или отключённым) экраном.
    FrameworkElement PinControl()
    {
        var pinned = S.Perf.Settings.PinScreen;
        var here = Body.Screen.Name;
        var screens = S.Monitors.All.Select(m => m.Name).ToList();
        var (onScreen, sleeping) = PinPlace.Where(pinned, screens);
        void Set(string? screen) => S.Config.Update(_ => S.Perf.Settings.PinScreen = screen);

        if (pinned is null)
            return PinButton("Закрепить на этом экране", false, () => Set(here));
        if (onScreen == here)
            return PinButton("Закреплён на этом экране", true, () => Set(null));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var number = Number(pinned);
        row.Children.Add(Text(sleeping ? $"Закреплён за экраном {number} — сейчас не подключён" : $"Закреплён за экраном {number}", 13, Sub));
        if (!sleeping) row.Children.Add(Btn("Сюда", click: () => Set(here)));
        row.Children.Add(Btn("Открепить", click: () => Set(null)));
        return row;
    }

    Button PinButton(string text, bool on, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Glyph("", 14, on ? Accent : Ink));
        content.Children.Add(Text(text, 13));
        var b = new Button
        {
            Content = content,
            Background = B(on ? PinBg : Chip),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 7, 12, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(b, on ? "Закреплён на этом экране — нажми, чтобы открепить" : text);
        b.Click += (_, _) => click();
        return b;
    }

    /// Номер экрана для подписи: по порядку мониторов, у отключённого — из имени (\\.\DISPLAY2 → 2).
    string Number(string name)
    {
        var all = S.Monitors.All;
        for (var i = 0; i < all.Count; i++) if (all[i].Name == name) return (i + 1).ToString();
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length > 0 ? digits : name;
    }

    UIElement FpsBlock((string Name, string Exe, string Why) game)
    {
        var perf = S.Perf;
        var stack = new StackPanel { Spacing = 8 };
        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fps = MakeCard("FPS", out var c);
        fps.MinWidth = 150;
        _cards["fps"] = c;
        top.Children.Add(fps);

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text(game.Name, 14, bold: true));
        info.Children.Add(Text($"{Path.GetFileName(game.Exe)} — игра: {game.Why}", 12, Sub, wrap: true));
        switch (perf.FpsState)
        {
            case FpsState.Denied:
                info.Children.Add(Text(PerfAccess.Current == PerfAccess.State.AfterSignIn
                    ? "Разрешение дано — FPS появится после выхода из Windows и входа снова."
                    : "Нет разрешения на счётчик кадров.", 12, AmberText, wrap: true));
                break;
            case FpsState.NoData:
                info.Children.Add(Text("Игра не отдаёт кадры (так бывает в эксклюзивном полноэкранном режиме) — нет данных.", 12, AmberText, wrap: true));
                break;
        }
        Grid.SetColumn(info, 1);
        top.Children.Add(info);

        var actions = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (perf.FpsState == FpsState.Denied && PerfAccess.Current == PerfAccess.State.Needed)
            actions.Children.Add(Btn("Разрешить", primary: true, click: async () => { if (await PerfAccess.Grant()) Rebuild(); }));
        actions.Children.Add(Btn("Это не игра", click: () => perf.Mark(game.Exe, false)));
        Grid.SetColumn(actions, 2);
        top.Children.Add(actions);
        stack.Children.Add(top);
        return Box(stack);
    }

    Border MakeCard(string label, out Card card)
    {
        var value = Text("—", 24, bold: true);
        var sub = Text("", 12, Sub);
        var spark = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 28, VerticalAlignment = VerticalAlignment.Bottom };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Text(label, 12, Sub));
        stack.Children.Add(value);
        stack.Children.Add(sub);
        stack.Children.Add(spark);
        card = new Card { Value = value, Sub = sub, Spark = spark };
        return new Border { Background = B(Color.FromArgb(255, 0x16, 0x16, 0x18)), CornerRadius = new CornerRadius(16), Padding = new Thickness(12), Child = stack };
    }

    static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    static string Gb(double bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", Ru);

    /// Обновить цифры на месте.
    void Paint()
    {
        var perf = S.Perf;
        var now = perf.Now;
        var smp = now.Sample;
        var hist = perf.History;
        void Set(string id, string value, string sub, IEnumerable<double> spark, double max, Color? color = null)
        {
            if (!_cards.TryGetValue(id, out var c)) return;
            c.Value.Text = value;
            c.Value.Foreground = B(color ?? Ink);
            c.Sub.Text = sub;
            var vals = spark.TakeLast(Bars).ToList();
            c.Spark.Children.Clear();
            foreach (var v in vals)
                c.Spark.Children.Add(new Border
                {
                    Width = 6,
                    Height = Math.Max(2, 28 * Math.Clamp(max > 0 ? v / max : 0, 0, 1)),
                    CornerRadius = new CornerRadius(2),
                    Background = B(Accent),
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
        }
        static string Temp(double? t) => t is { } v ? $"{v:0} °C" : "";
        static Color? TempColor(double? t) => t switch { >= 90 => Red, >= 80 => Amber, _ => null };

        Set("cpu", smp is null ? "—" : $"{smp.Cpu:0} %", Temp(smp?.CpuTemp), hist.Select(h => h.Cpu), 100, TempColor(smp?.CpuTemp));
        Set("gpu", smp?.Gpu is { } g ? $"{g:0} %" : "—", Temp(smp?.GpuTemp), hist.Select(h => h.Gpu ?? 0), 100, TempColor(smp?.GpuTemp));
        Set("ram", smp is null ? "—" : Gb(smp.RamUsed), smp is null ? "" : $"из {Math.Round(smp.RamTotal / (1024.0 * 1024 * 1024)):0} ГБ",
            hist.Select(h => h.RamUsed), smp?.RamTotal ?? 1);
        Set("vram", smp?.VramUsed is { } v ? Gb(v) : "—", smp?.VramTotal is { } vt ? $"из {Math.Round(vt / (1024.0 * 1024 * 1024)):0} ГБ" : "",
            hist.Select(h => h.VramUsed ?? 0), smp?.VramTotal ?? 1);
        if (_cards.ContainsKey("fps"))
        {
            var low = now.Low1 is { } l ? $"{l:0}" : "—";
            var ms = now.FrameMs is { } m ? m.ToString("0.0", Ru) + " мс" : "";
            Set("fps", now.Fps is { } f ? $"{f:0}" : "—", $"1% low {low}{(ms.Length > 0 ? " · " + ms : "")}",
                perf.FpsHistory.Select(x => x ?? 0), Math.Max(1, perf.FpsHistory.Count == 0 ? 0 : perf.FpsHistory.Max(x => x ?? 0)));
        }
    }
}
