using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Charge;
using WindowsShell.Config;
using WindowsShell.Ui;
using static WindowsShell.Island.IslandUi;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Island.Widgets;

/// Заряд в острове (эскиз «Заряд»): батарея ноутбука сверху (если есть), устройства с полоской
/// и %, севшие янтарные, отключённые приглушены. Правый клик — свой порог или «Не следить».
/// Внизу — общий порог чипами.
public sealed class ChargeWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    const double BarW = 150;

    public override string Id => "charge";
    public override double Width => 560;

    protected override void OnOpen()
    {
        S.Charge.Changed += Later;
        S.Battery.Changed += Later;
        S.Charge.Refresh();
    }

    protected override void OnClose()
    {
        S.Charge.Changed -= Later;
        S.Battery.Changed -= Later;
    }

    protected override void Build()
    {
        var list = S.Charge.Watched.ToList();
        var low = list.Count(S.Charge.IsLow);
        Root.Children.Add(Header("Заряд", out var right, low > 0 ? AmberText : Sub));
        right.Text = list.Count == 0 ? "" : low > 0 ? $"пора зарядить: {low}" : "всё заряжено";

        var battery = S.Battery.State;
        if (battery.Present) Root.Children.Add(GroupBox(BatteryRow(battery)));

        var rows = new StackPanel { Spacing = 2 };
        if (list.Count == 0)
            rows.Children.Add(new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = Text("Устройства с батареей появятся здесь сами: Bluetooth-клавиатуры, мыши, геймпады, наушники, мыши и гарнитуры Logitech.", 13, Sub, wrap: true),
            });
        foreach (var d in list) rows.Children.Add(DeviceRow(d));
        Root.Children.Add(GroupBox(rows));

        // Общий порог.
        var th = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        th.Children.Add(new Border { Padding = new Thickness(4, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Child = Text("Предупреждать ниже", 13, Sub) });
        var current = S.Charge.Threshold;
        foreach (var n in ChargeSettings.Thresholds)
        {
            var value = n;
            var b = Btn($"{n} %", primary: n == current, () =>
            {
                S.Config.Update(_ => S.Charge.Settings.Threshold = value);
                Rebuild();
            });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"Предупреждать ниже {n} %");
            th.Children.Add(b);
        }
        Root.Children.Add(new ScrollViewer
        {
            Content = th,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
        });

        var hidden = S.Charge.Settings.Ignored.Count;
        var links = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 4 };
        if (hidden > 0) links.Children.Add(Link($"Скрыто: {hidden} — в настройках", () => { Body.Sleep(restoreFocus: false); S.OpenSettings("widgets"); }));
        links.Children.Add(Link("Bluetooth и устройства ↗", () => { Body.Sleep(restoreFocus: false); Launcher.Open("ms-settings:bluetooth"); }));
        Root.Children.Add(links);
    }

    UIElement BatteryRow(Tail.BatteryState b)
    {
        var row = Grid3();
        row.Padding = new Thickness(12, 10, 12, 10);
        row.Children.Add(Glyph(b.Glyph, 20, b.Percent <= 20 && !b.Plugged ? Amber : Ink));
        var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text("Этот компьютер", 14));
        var sub = b.Charging ? "заряжается" : b.Plugged ? "от сети" : b.SecondsLeft > 0 ? $"осталось {TimeLeft(b.SecondsLeft)}" : "от батареи";
        text.Children.Add(Text(sub, 12, Sub));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(Bar(b.Percent / 100.0, b.Percent <= 20 && !b.Plugged ? Amber : Green, BarW));
        right.Children.Add(Text($"{b.Percent} %", 14, bold: true));
        Grid.SetColumn(right, 2);
        row.Children.Add(right);
        return row;
    }

    static string TimeLeft(int s) => s >= 3600 ? $"{s / 3600} ч {s % 3600 / 60} мин" : $"{Math.Max(1, s / 60)} мин";

    static Grid Grid3()
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return g;
    }

    UIElement DeviceRow(ChargeDevice d)
    {
        var low = S.Charge.IsLow(d);
        var own = S.Charge.Settings.DeviceThreshold(d.Key);
        var dim = !d.Connected;
        var g = Grid3();
        g.Opacity = dim ? 0.5 : 1;
        g.Children.Add(Glyph(d.Glyph, 18, low ? Amber : Ink));
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var name = Text(d.Name, 14);
        ToolTipService.SetToolTip(name, d.Source.Length > 0 ? $"{d.Title}\nДанные: {d.Source}" : d.Title);
        text.Children.Add(name);
        var status = d switch
        {
            { Connected: false } => "не подключено",
            { Charging: true } => "заряжается",
            { Percent: null, Detail: null } => "заряд неизвестен",
            _ when low => own is { } n ? $"пора зарядить · свой порог {n} %" : "пора зарядить",
            _ => own is { } m ? $"свой порог {m} %" : null,
        };
        var sub = string.Join(" · ", new[] { d.Connected ? d.Detail : null, status }.Where(x => !string.IsNullOrEmpty(x)));
        if (sub.Length > 0) text.Children.Add(Text(sub, 12, low ? AmberText : Sub));
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        if (d.Percent is { } p) right.Children.Add(Bar(p / 100.0, low ? Amber : d.Charging ? Green : Ink, BarW));
        var percent = Text(d.Percent is { } v ? $"{v} %" : "—", 14, low ? AmberText : Ink, bold: true);
        percent.MinWidth = 48;
        percent.TextAlignment = TextAlignment.Right;
        right.Children.Add(percent);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);

        var row = Row(g, name: d.Connected && d.Percent is { } a ? $"{d.Title}, {a} %" : d.Title);
        row.RightTapped += (_, e) => { e.Handled = true; Menu(d, row, e.GetPosition(null)); };
        // С клавиатуры: Shift+F10 / клавиша меню.
        row.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            var at = row.TransformToVisual(null).TransformPoint(new Point(24, row.ActualHeight / 2));
            Menu(d, row, at);
        };
        return row;
    }

    void Menu(ChargeDevice d, FrameworkElement row, Point at)
    {
        var own = S.Charge.Settings.DeviceThreshold(d.Key);
        var levels = new List<MenuItem>
        {
            new() { Text = $"Как у всех ({S.Charge.Threshold} %)", Checked = own is null, Invoke = () => S.Charge.SetDeviceThreshold(d, null) },
            MenuItem.Sep,
        };
        foreach (var n in ChargeSettings.Thresholds)
        {
            var value = n;
            levels.Add(new MenuItem { Text = $"{n} %", Checked = own == n, Invoke = () => S.Charge.SetDeviceThreshold(d, value) });
        }
        var items = new List<MenuItem>
        {
            new() { Text = "Свой порог", Glyph = "", Children = levels },
            new() { Text = "Не следить за этим устройством", Glyph = "", Invoke = () => S.Charge.Ignore(d) },
        };
        var scale = Body.Screen.Scale;
        GetWindowRect(Body.Hwnd, out var r);
        S.Menu.Show(("island-charge", d.Key), new Rect(r.Left + at.X * scale, r.Top + at.Y * scale, 1, 1), items, Placement.AtPoint, owner: Body);
    }
}
