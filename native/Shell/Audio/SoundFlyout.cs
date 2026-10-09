using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsShell.Apps;
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
    bool _devicesOpen;
    bool _updating;
    Slider? _master;
    FontIcon? _masterIcon;
    TextBlock? _masterValue;
    Slider? _mic;
    FontIcon? _micIcon;
    string _sessionKey = "";

    public SoundFlyout(AudioService audio, Icons icons) : base(activating: true)
    {
        _audio = audio;
        _icons = icons;
        SetContent(_body);
        _audio.Changed += () => { if (IsOpen) Refresh(); };
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

    public void Show(object anchor, Rect anchorPx)
    {
        _devicesOpen = false;
        Build();
        Open(anchor, anchorPx);
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

        // Заголовок и устройство вывода.
        var devices = _audio.Devices();
        var current = _audio.DefaultId();
        var currentName = devices.FirstOrDefault(d => d.Id == current)?.Name ?? "Нет устройства вывода";
        var deviceRow = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Theme.Brush(Theme.Card),
            BorderThickness = new Thickness(0),
            Height = 44,
            Padding = new Thickness(12, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
        };
        var dGrid = new Grid { ColumnSpacing = 10 };
        dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dGrid.Children.Add(new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 16 });
        var dName = Label(currentName);
        Grid.SetColumn(dName, 1);
        dGrid.Children.Add(dName);
        var chevron = new FontIcon { Glyph = _devicesOpen ? "" : "", FontFamily = Theme.Icons, FontSize = 12 };
        Grid.SetColumn(chevron, 2);
        dGrid.Children.Add(chevron);
        deviceRow.Content = dGrid;
        deviceRow.Click += (_, _) => { _devicesOpen = !_devicesOpen; Build(); Layout(); };
        _body.Children.Add(deviceRow);

        if (_devicesOpen)
        {
            foreach (var d in devices)
            {
                var item = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Height = 36,
                    Padding = new Thickness(12, 0, 10, 0),
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(new FontIcon
                {
                    Glyph = d.Id == current ? "" : "",
                    FontFamily = Theme.Icons,
                    FontSize = 14,
                    Width = 16,
                    Foreground = Theme.Brush(Theme.Accent),
                });
                row.Children.Add(Label(d.Name));
                item.Content = row;
                var id = d.Id;
                item.Click += (_, _) => { _audio.SetDefault(id); _devicesOpen = false; Build(); Layout(); };
                _body.Children.Add(item);
            }
        }

        // Общая громкость.
        var master = new Grid { Height = 52, ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
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

        // Приложения.
        if (sessions.Count > 0)
        {
            var head = Label("Приложения", 12, secondary: true);
            head.Margin = new Thickness(4, 8, 0, 2);
            _body.Children.Add(head);
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
                    _icons.Get(s.Exe, bmp => img.Source = bmp);
                    icon = img;
                }
                else icon = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 16 };
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
        if (_audio.HasInput)
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
