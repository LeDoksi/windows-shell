using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Apps;
using WindowsShell.Audio;
using WindowsShell.Config;
using WindowsShell.Ui;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island.Widgets;

/// Звук в острове: функции те же, что у окошка в доке (SoundFlyout), вид — по эскизу.
/// Общая громкость первой, вывод — кнопками в один клик, профили, микрофон, громкость
/// приложений свёрнута (редко нужна).
public sealed class SoundWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    bool _appsOpen, _updating;
    string? _shownDefault;
    string _sessionKey = "";
    Slider? _master, _mic;
    TextBlock? _value;
    FontIcon? _muteIcon, _micIcon;
    DispatcherQueueTimer? _sessions;

    public override string Id => "sound";
    public override double Width => 560;

    protected override void OnOpen()
    {
        S.Audio.Changed += OnAudio;
        _sessions ??= S.Queue.CreateTimer();
        _sessions.Interval = TimeSpan.FromSeconds(1);
        _sessions.Tick -= OnSessionsTick;
        _sessions.Tick += OnSessionsTick;
        _sessions.Start();
    }

    protected override void OnClose()
    {
        S.Audio.Changed -= OnAudio;
        _sessions?.Stop();
    }

    void OnSessionsTick(DispatcherQueueTimer t, object o)
    {
        if (SessionKey() != _sessionKey) Rebuild();
    }

    string SessionKey() => string.Join("|", S.Audio.Sessions().Select(x => x.Pid));

    void OnAudio() => S.Queue.TryEnqueue(() =>
    {
        if (!IsOpen) return;
        if (S.Audio.DefaultId() != _shownDefault) Rebuild();
        else Refresh();
    });

    static string VolumeGlyph(int v, bool muted) =>
        muted ? "" : v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";

    protected override void Build()
    {
        var a = S.Audio;
        var settings = S.AudioSettings;
        var sessions = a.Sessions();
        _sessionKey = string.Join("|", sessions.Select(x => x.Pid));

        Root.Children.Add(Header("Звук", out var value));
        _value = value;
        _value.Text = a.Muted ? "выключен" : $"{a.Volume} %";

        var master = new Grid { ColumnSpacing = 12 };
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mute = Round(VolumeGlyph(a.Volume, a.Muted), 40, a.Muted ? "Включить звук" : "Выключить звук", out var muteIcon);
        _muteIcon = muteIcon;
        mute.Click += (_, _) => a.Muted = !a.Muted;
        master.Children.Add(mute);
        _master = IslandUi.Slider(a.Volume);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_master, "Громкость");
        _master.ValueChanged += (_, e) => { if (!_updating) a.Volume = (int)e.NewValue; };
        Grid.SetColumn(_master, 1);
        master.Children.Add(_master);
        Root.Children.Add(master);

        // Профили: устройство и громкость одним кликом.
        if (settings.Profiles.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var activeId = a.DefaultId();
            foreach (var p in settings.Profiles)
            {
                var on = p.DeviceId == activeId && p.Volume == a.Volume;
                var b = Btn(p.Name, primary: on);
                var profile = p;
                b.Click += (_, _) => { SoundFlyout.ApplyProfile(a, profile); Rebuild(); };
                row.Children.Add(b);
            }
            Root.Children.Add(new ScrollViewer
            {
                Content = row,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
            });
        }

        // Вывод — кнопками сразу, без выпадающего списка.
        var current = a.DefaultId();
        _shownDefault = current;
        var outs = new StackPanel { Spacing = 8 };
        outs.Children.Add(Text("Вывод — в один клик", 12, Sub));
        var devices = a.Devices();
        if (devices.Count == 0) outs.Children.Add(Text("Нет устройства вывода", 13, Sub));
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        const int perRow = 3;
        for (var c = 0; c < Math.Min(perRow, Math.Max(1, devices.Count)); c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < devices.Count; i++)
        {
            if (i % perRow == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var d = devices[i];
            var on = d.Id == current;
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(Glyph(DeviceGlyph(d.Name), 16, on ? Ink : Sub));
            var name = Text(d.Name, 13);
            ToolTipService.SetToolTip(name, d.Name);
            Grid.SetColumn(name, 1);
            content.Children.Add(name);
            if (on)
            {
                var check = Glyph("", 12);
                Grid.SetColumn(check, 2);
                content.Children.Add(check);
            }
            var b = Row(content, name: d.Name);
            b.Background = B(on ? SelectedSlot : Group);
            var id = d.Id;
            b.Click += (_, _) => { if (id != a.DefaultId()) { a.SetDefault(id); Rebuild(); } };
            Grid.SetRow(b, i / perRow);
            Grid.SetColumn(b, i % perRow);
            grid.Children.Add(b);
        }
        outs.Children.Add(grid);
        Root.Children.Add(outs);

        // Микрофон.
        if (a.HasInput && settings.ShowMic)
        {
            var mic = new Grid { ColumnSpacing = 12 };
            mic.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            mic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var micMute = Round(a.MicMuted ? "" : "", 36, a.MicMuted ? "Включить микрофон" : "Выключить микрофон", out var micIcon);
            _micIcon = micIcon;
            micMute.Click += (_, _) => a.MicMuted = !a.MicMuted;
            mic.Children.Add(micMute);
            var label = Text("Микрофон", 14);
            Grid.SetColumn(label, 1);
            mic.Children.Add(label);
            _mic = IslandUi.Slider(a.MicVolume);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_mic, "Громкость микрофона");
            var micValue = Text($"{a.MicVolume} %", 13, Sub);
            micValue.HorizontalAlignment = HorizontalAlignment.Right;
            _mic.ValueChanged += (_, e) => { if (!_updating) a.MicVolume = (int)e.NewValue; micValue.Text = $"{(int)e.NewValue} %"; };
            Grid.SetColumn(_mic, 2);
            mic.Children.Add(_mic);
            Grid.SetColumn(micValue, 3);
            mic.Children.Add(micValue);
            Root.Children.Add(mic);
        }

        // Громкость приложений — свёрнута.
        if (sessions.Count > 0 && settings.ShowApps)
        {
            var list = new StackPanel { Spacing = 2 };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(Text($"Громкость приложений · {sessions.Count}", 13, Color.FromArgb(255, 0xD9, 0xD9, 0xDE)));
            var chevron = Glyph(_appsOpen ? "" : "", 11, Sub);
            Grid.SetColumn(chevron, 1);
            head.Children.Add(chevron);
            var toggle = Row(head, name: "Громкость приложений");
            toggle.Click += (_, _) => { _appsOpen = !_appsOpen; Rebuild(); };
            list.Children.Add(toggle);
            if (_appsOpen)
                foreach (var x in sessions)
                {
                    var row = new Grid { Height = 44, ColumnSpacing = 10, Margin = new Thickness(10, 0, 10, 0) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                    UIElement icon;
                    if (x.Exe != null)
                    {
                        var img = new Image { Width = 22, Height = 22 };
                        S.Icons.Get(x.Exe, (int)Math.Ceiling(22 * Body.Screen.Scale), bmp => img.Source = bmp);
                        icon = img;
                    }
                    else icon = Glyph("", 16);
                    row.Children.Add(icon);
                    var name = Text(x.Name, 13);
                    ToolTipService.SetToolTip(name, x.Name);
                    Grid.SetColumn(name, 1);
                    row.Children.Add(name);
                    var slider = IslandUi.Slider(Math.Round(x.Level * 100));
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(slider, $"Громкость: {x.Name}");
                    var v = Text(((int)Math.Round(x.Level * 100)).ToString(), 13, Sub);
                    v.HorizontalAlignment = HorizontalAlignment.Right;
                    var session = x;
                    slider.ValueChanged += (_, e) => { session.Level = (float)(e.NewValue / 100); v.Text = ((int)e.NewValue).ToString(); };
                    Grid.SetColumn(slider, 2);
                    row.Children.Add(slider);
                    Grid.SetColumn(v, 3);
                    row.Children.Add(v);
                    list.Children.Add(row);
                }
            Root.Children.Add(GroupBox(list));
        }

        Root.Children.Add(Link("Параметры звука Windows ↗", () => { Body.Sleep(restoreFocus: false); Launcher.Open("ms-settings:sound"); }));
    }

    static string DeviceGlyph(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("науш") || n.Contains("headphone") || n.Contains("headset") || n.Contains("гарнит") || n.Contains("airpods") || n.Contains("buds")) return "";
        if (n.Contains("монитор") || n.Contains("display") || n.Contains("hdmi") || n.Contains("tv")) return "";
        return "";
    }

    /// Звук поменяли снаружи (клавиши, микшер): ползунки идут за ним без пересборки.
    void Refresh()
    {
        _updating = true;
        var a = S.Audio;
        if (_master != null && Math.Abs(_master.Value - a.Volume) >= 1) _master.Value = a.Volume;
        if (_value != null) _value.Text = a.Muted ? "выключен" : $"{a.Volume} %";
        if (_muteIcon != null) _muteIcon.Glyph = VolumeGlyph(a.Volume, a.Muted);
        if (_mic != null && Math.Abs(_mic.Value - a.MicVolume) >= 1) _mic.Value = a.MicVolume;
        if (_micIcon != null) _micIcon.Glyph = a.MicMuted ? "" : "";
        _updating = false;
    }
}
