using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Tail.FlyoutUi;

namespace WindowsShell.Tail;

public sealed record BatteryState(bool Present, int Percent, bool Charging, bool Plugged, int SecondsLeft)
{
    /// Значок уровня: 10 ступеней, при зарядке — с молнией.
    public string Glyph
    {
        get
        {
            // Battery0…9 = E850…E859, Battery10 = E83F; с молнией BatteryCharging0…9 = E85A…E863.
            var step = Math.Clamp((int)Math.Round(Percent / 10.0), 0, 10);
            if (Charging) return char.ConvertFromUtf32(0xE85A + Math.Min(step, 9));
            return step == 10 ? "\uE83F" : char.ConvertFromUtf32(0xE850 + step);
        }
    }

    /// 20 % и меньше — оранжевая, 10 % и меньше — красная (без зарядки).
    public Windows.UI.Color Color => Plugged ? Theme.Text : Percent <= 10 ? Windows.UI.Color.FromArgb(255, 0xFF, 0x5F, 0x5F) : Percent <= 20 ? Theme.Warning : Theme.Text;
}

/// Батарея (раздел 2.4, WSH-50): только на ноутбуке. На ПК без батареи значка нет вообще.
public sealed class BatteryService
{
    public BatteryState State { get; private set; } = Read();
    public event Action? Changed;

    public BatteryService(DispatcherQueue queue, MessageWindow messages)
    {
        // Подключили или отключили зарядку, уровень сменился — Windows присылает WM_POWERBROADCAST.
        messages.Message += (msg, w, _) => { if (msg == 0x0218 && (int)w == 0xA /* PBT_APMPOWERSTATUSCHANGE */) Refresh(); };
        messages.Resumed += Refresh;
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(30);
        timer.Tick += (_, _) => Refresh();
        if (State.Present) timer.Start();
    }

    public void Refresh()
    {
        var next = Read();
        if (next == State) return;
        State = next;
        Changed?.Invoke();
    }

    static BatteryState Read()
    {
        if (!GetSystemPowerStatus(out var s)) return new(false, 0, false, false, -1);
        // 128 — батареи нет, 255 — неизвестно.
        var present = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
        return new(present, s.BatteryLifePercent > 100 ? 0 : s.BatteryLifePercent, (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1, s.BatteryLifeTime);
    }

    // ---------- режим питания (Экономия / Сбалансированный / Производительность) ----------

    public static readonly (Guid Id, string Name)[] Modes =
    [
        (new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), "Экономия"),
        (Guid.Empty, "Сбалансированный"),
        (new Guid("ded574b5-45a0-4f42-8737-46345c09c238"), "Производительность"),
    ];

    public static Guid? Mode
    {
        get
        {
            try { return PowerGetEffectiveOverlayScheme(out var g) == 0 ? g : null; }
            catch { return null; }
        }
        set
        {
            try { if (value is { } g) PowerSetActiveOverlayScheme(g); }
            catch (Exception e) { Log.Error("режим питания", e); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveOverlayScheme(Guid scheme);
}

/// Окошко батареи: процент, время до разряда или заряда, режим питания.
public sealed class BatteryFlyout : Popup
{
    readonly BatteryService _battery;
    readonly StackPanel _body = new() { Padding = new Thickness(16, 14, 16, 10), Spacing = 6 };

    public BatteryFlyout(BatteryService battery) : base(activating: true)
    {
        _battery = battery;
        SetContent(_body);
        _battery.Changed += () => { if (IsOpen) { Build(); Layout(); } };
    }

    protected override double Width => 320;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(width, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    public void Show(object anchor, Rect anchorPx, object? owner)
    {
        Build();
        Open(anchor, anchorPx, owner: owner);
    }

    void Build()
    {
        _body.Children.Clear();
        var s = _battery.State;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var icon = Icon(s.Glyph, 28);
        icon.Foreground = Theme.Brush(s.Color);
        head.Children.Add(icon);
        head.Children.Add(Label($"{s.Percent} %", 28, bold: true));
        _body.Children.Add(head);
        _body.Children.Add(Label(TimeText(s), 13, secondary: true));

        _body.Children.Add(Separator());
        _body.Children.Add(Label("Режим питания", 12, secondary: true));
        var current = BatteryService.Mode;
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (id, name) in BatteryService.Modes)
        {
            combo.Items.Add(name);
            if (current == id) combo.SelectedIndex = combo.Items.Count - 1;
        }
        combo.IsEnabled = current != null;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) BatteryService.Mode = BatteryService.Modes[combo.SelectedIndex].Id;
        };
        _body.Children.Add(combo);
        _body.Children.Add(Link("Параметры питания и батареи", () => { Close(restoreFocus: false); Launcher.Open("ms-settings:batterysaver"); }));
    }

    static string TimeText(BatteryState s)
    {
        if (s.Charging) return s.Percent >= 100 ? "Полностью заряжена" : "Заряжается";
        if (s.Plugged) return "Подключена к сети";
        if (s.SecondsLeft <= 0) return "Работает от батареи";
        var t = TimeSpan.FromSeconds(s.SecondsLeft);
        return t.TotalHours >= 1 ? $"Осталось {(int)t.TotalHours} ч {t.Minutes} мин" : $"Осталось {t.Minutes} мин";
    }
}
