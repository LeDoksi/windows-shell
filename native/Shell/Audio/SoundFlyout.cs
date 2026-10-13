using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Config;
using WindowsShell.Ui;

namespace WindowsShell.Audio;

/// Окошко звука (раздел 2.3) на родных контролах WinUI: устройство вывода, общая громкость,
/// громкость приложений, микрофон. Всё обновляется сразу, если звук меняют снаружи.
public sealed class SoundFlyout : Popup
{
    readonly AudioService _audio;
    readonly Icons _icons;
    readonly StackPanel _body = new() { Padding = new Thickness(16, 14, 16, 12), Spacing = 4 };
    readonly DispatcherQueueTimer _sessionsTimer;
    bool _appsOpen;
    string? _shownDefault;
    bool _updating;
    Slider? _master;
    FontIcon? _masterIcon;
    TextBlock? _masterValue;
    Slider? _mic;
    FontIcon? _micIcon;
    string _sessionKey = "";

    readonly ShellConfig _config;

    public SoundFlyout(AudioService audio, Icons icons, ShellConfig config) : base(activating: true)
    {
        _audio = audio;
        _icons = icons;
        _config = config;
        SetContent(_body);
        // Выход переключили снаружи (горячая клавиша, Параметры): список пересобирается.
        _audio.Changed += () =>
        {
            if (!IsOpen) return;
            if (_audio.DefaultId() != _shownDefault) { Build(); Layout(); }
            else Refresh();
        };
        _sessionsTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _sessionsTimer.Interval = TimeSpan.FromSeconds(1);
        // Новое приложение заиграло, пока окошко открыто, — появляется в списке.
        _sessionsTimer.Tick += (_, _) => { if (IsOpen && SessionKey() != _sessionKey) { Build(); Layout(); } };
    }

    protected override double Width => 360;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(width, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    public void Show(object anchor, Rect anchorPx, object? owner = null)
    {
        Build();
        Open(anchor, anchorPx, owner: owner);
        _sessionsTimer.Start();
    }

    protected override void OnClosing() => _sessionsTimer.Stop();

    string SessionKey() => string.Join("|", _audio.Sessions().Select(s => s.Pid));

    static string VolumeGlyph(int v, bool muted) =>
        muted ? "" : v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";

    static TextBlock Label(string text, double size = 14, bool secondary = false) => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        Foreground = Theme.Brush(secondary ? Theme.TextSecondary : Theme.Text),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Button IconButton(string glyph, out FontIcon icon)
    {
        icon = new FontIcon { Glyph = glyph, FontFamily = Theme.Icons, FontSize = 16 };
        return new Button
        {
            Content = icon,
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static Slider MakeSlider(double value) => new()
    {
        Minimum = 0,
        Maximum = 100,
        StepFrequency = 1,
        SmallChange = 2,
        LargeChange = 10,
        Value = value,
        VerticalAlignment = VerticalAlignment.Center,
        IsThumbToolTipEnabled = false,
    };

    void Build()
    {
        _body.Children.Clear();
        var sessions = _audio.Sessions();
        _sessionKey = string.Join("|", sessions.Select(s => s.Pid));

        // Общая громкость — первой: ей пользуются чаще всего.
        var master = new Grid { Height = 52, ColumnSpacing = 8 };
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        var mute = IconButton(VolumeGlyph(_audio.Volume, _audio.Muted), out var masterIcon);
        _masterIcon = masterIcon;
        mute.Click += (_, _) => _audio.Muted = !_audio.Muted;
        master.Children.Add(mute);
        _master = MakeSlider(_audio.Volume);
        _master.ValueChanged += (_, e) => { if (!_updating) _audio.Volume = (int)e.NewValue; };
        Grid.SetColumn(_master, 1);
        master.Children.Add(_master);
        _masterValue = Label(_audio.Volume.ToString(), 14, secondary: true);
        _masterValue.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_masterValue, 2);
        master.Children.Add(_masterValue);
        _body.Children.Add(master);

        var settings = new AudioSettings(_config.Widget("audio"));
        var devices = _audio.Devices();

        // Профили: устройство и громкость одним кликом.
        var profiles = settings.Profiles;
        if (profiles.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 4) };
            var activeId = _audio.DefaultId();
            foreach (var p in profiles)
            {
                var on = p.DeviceId == activeId && p.Volume == _audio.Volume;
                var b = new Button
                {
                    Content = Label(p.Name, 13),
                    Height = 32,
                    Padding = new Thickness(12, 0, 12, 0),
                    CornerRadius = new CornerRadius(16),
                    Background = Theme.Brush(on ? Theme.Accent : Theme.Card),
                    Foreground = Theme.Brush(on ? Microsoft.UI.Colors.Black : Theme.Text),
                    BorderThickness = new Thickness(0),
                };
                if (on && b.Content is TextBlock t) t.Foreground = Theme.Brush(Microsoft.UI.Colors.Black);
                var profile = p;
                b.Click += (_, _) => { ApplyProfile(_audio, profile); Build(); Layout(); };
                row.Children.Add(b);
            }
            _body.Children.Add(new ScrollViewer
            {
                Content = row,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
            });
        }

        // Устройства вывода — сразу списком кнопок: переключение одним кликом.
        var current = _audio.DefaultId();
        _shownDefault = current;
        var outHead = Label("Вывод звука", 12, secondary: true);
        outHead.Margin = new Thickness(4, 6, 0, 2);
        _body.Children.Add(outHead);
        if (devices.Count == 0) _body.Children.Add(Label("Нет устройства вывода", 13, secondary: true));
        foreach (var d in devices)
        {
            var active = d.Id == current;
            var item = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Theme.Brush(active ? Theme.Selected : Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Height = 40,
                Padding = new Thickness(12, 0, 10, 0),
            };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new FontIcon
            {
                Glyph = "\uE7F5",
                FontFamily = Theme.Icons,
                FontSize = 16,
                Foreground = Theme.Brush(active ? Theme.Accent : Theme.TextSecondary),
            });
            var name = Label(d.Name);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (active)
            {
                // Полоска акцента слева, как у выбранного пункта в Windows 11.
                var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5,
                    Fill = Theme.Brush(Theme.Accent),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(-10, 0, 0, 0),
                };
                row.Children.Add(bar);
            }
            item.Content = row;
            var id = d.Id;
            item.Click += (_, _) => { if (id != _audio.DefaultId()) { _audio.SetDefault(id); Build(); Layout(); } };
            _body.Children.Add(item);
        }

        // Громкость приложений нужна редко: свёрнута, раскрывается по клику.
        if (sessions.Count > 0 && settings.ShowApps)
        {
            var toggle = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Height = 36,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(4, 0, 10, 0),
            };
            var tGrid = new Grid();
            tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tGrid.Children.Add(Label($"Громкость приложений ({sessions.Count})", 12, secondary: true));
            var chevron = new FontIcon { Glyph = _appsOpen ? "\uE70E" : "\uE70D", FontFamily = Theme.Icons, FontSize = 11, Foreground = Theme.Brush(Theme.TextSecondary) };
            Grid.SetColumn(chevron, 1);
            tGrid.Children.Add(chevron);
            toggle.Content = tGrid;
            toggle.Click += (_, _) => { _appsOpen = !_appsOpen; Build(); Layout(); };
            _body.Children.Add(toggle);
        }
        if (_appsOpen && settings.ShowApps)
        {
            foreach (var s in sessions)
            {
                var row = new Grid { Height = 44, ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                UIElement icon;
                if (s.Exe != null)
                {
                    var img = new Image { Width = 22, Height = 22 };
                    _icons.Get(s.Exe, (int)Math.Ceiling(22 * Win.Scale), bmp => img.Source = bmp);
                    icon = img;
                }
                else icon = new FontIcon { Glyph = "\uE8D6", FontFamily = Theme.Icons, FontSize = 16 };
                row.Children.Add(icon);
                var name = Label(s.Name, 13);
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var slider = MakeSlider(Math.Round(s.Level * 100));
                var value = Label(((int)Math.Round(s.Level * 100)).ToString(), 13, secondary: true);
                value.HorizontalAlignment = HorizontalAlignment.Right;
                var session = s;
                slider.ValueChanged += (_, e) => { session.Level = (float)(e.NewValue / 100); value.Text = ((int)e.NewValue).ToString(); };
                Grid.SetColumn(slider, 2);
                row.Children.Add(slider);
                Grid.SetColumn(value, 3);
                row.Children.Add(value);
                _body.Children.Add(row);
            }
        }

        // Микрофон.
        if (_audio.HasInput && settings.ShowMic)
        {
            var head = Label("Микрофон", 12, secondary: true);
            head.Margin = new Thickness(4, 8, 0, 2);
            _body.Children.Add(head);
            var mic = new Grid { Height = 44, ColumnSpacing = 8 };
            mic.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            mic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            var micMute = IconButton(_audio.MicMuted ? "" : "", out var micIcon);
            _micIcon = micIcon;
            micMute.Click += (_, _) => _audio.MicMuted = !_audio.MicMuted;
            mic.Children.Add(micMute);
            _mic = MakeSlider(_audio.MicVolume);
            var micValue = Label(_audio.MicVolume.ToString(), 14, secondary: true);
            micValue.HorizontalAlignment = HorizontalAlignment.Right;
            _mic.ValueChanged += (_, e) => { if (!_updating) _audio.MicVolume = (int)e.NewValue; micValue.Text = ((int)e.NewValue).ToString(); };
            Grid.SetColumn(_mic, 1);
            mic.Children.Add(_mic);
            Grid.SetColumn(micValue, 2);
            mic.Children.Add(micValue);
            _body.Children.Add(mic);
        }

        // Ссылка на параметры Windows.
        var footer = new HyperlinkButton
        {
            Content = Label("Параметры звука", 13),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(4, 4, 4, 4),
            Height = 32,
        };
        footer.Click += (_, _) => { Close(restoreFocus: false); Launcher.Open("ms-settings:sound"); };
        _body.Children.Add(footer);
    }

    /// Профиль: устройство по умолчанию и громкость.
    public static void ApplyProfile(AudioService audio, AudioProfile p)
    {
        if (audio.DefaultId() != p.DeviceId) audio.SetDefault(p.DeviceId);
        // Громкость — уже у нового устройства (подписка переезжает на него сразу).
        audio.Rebind();
        audio.Volume = p.Volume;
    }

    /// Звук поменяли снаружи (медиаклавиши, микшер): ползунки идут за ним без пересборки.
    void Refresh()
    {
        _updating = true;
        var v = _audio.Volume;
        if (_master != null && Math.Abs(_master.Value - v) >= 1) _master.Value = v;
        if (_masterValue != null) _masterValue.Text = v.ToString();
        if (_masterIcon != null) _masterIcon.Glyph = VolumeGlyph(v, _audio.Muted);
        if (_mic != null && Math.Abs(_mic.Value - _audio.MicVolume) >= 1) _mic.Value = _audio.MicVolume;
        if (_micIcon != null) _micIcon.Glyph = _audio.MicMuted ? "" : "";
        _updating = false;
    }
}
