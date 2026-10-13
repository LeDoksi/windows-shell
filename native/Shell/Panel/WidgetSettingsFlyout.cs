using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WindowsShell.Config;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Panel;

/// «Настройки виджетов» под ⚙ на панели: какие виджеты включены и настройки звука
/// (шаг колеса, что показывать в окошке, профили и их горячие клавиши).
public sealed class WidgetSettingsFlyout : Popup
{
    readonly Services _s;
    readonly StackPanel _body = new() { Padding = new Thickness(16, 14, 16, 14), Spacing = 6 };

    public WidgetSettingsFlyout(Services services) : base(activating: true)
    {
        _s = services;
        SetContent(new ScrollViewer { Content = _body, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    protected override double Width => 400;
    protected override double Gap => 8;

    public void Show(Rect anchorPx, object? owner)
    {
        Build();
        Open(this, anchorPx, owner is null ? Placement.Above : Placement.Below, owner: owner);
    }

    static TextBlock Text(string text, double size = 14, bool secondary = false, bool bold = false) => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Theme.Brush(secondary ? Theme.TextSecondary : Theme.Text),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// Строка «название, пояснение — переключатель».
    static Grid SwitchRow(string title, string? hint, bool on, Action<bool> changed)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 44 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Text(title) } };
        if (hint != null) text.Children.Add(Text(hint, 12, secondary: true));
        row.Children.Add(text);
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        // Включённый — нашим акцентом, как в Windows 11 (иначе он серый и не видно, что включён).
        foreach (var key in new[] { "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed" })
            toggle.Resources[key] = Theme.Brush(Theme.Accent);
        foreach (var key in new[] { "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed" })
            toggle.Resources[key] = Theme.Brush(Microsoft.UI.Colors.Black);
        toggle.Resources["ToggleSwitchStrokeOn"] = Theme.Brush(Theme.Accent);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(Text("Виджеты", 16, bold: true));
        var audioOn = _s.Config.WidgetEnabled("audio");
        _body.Children.Add(SwitchRow("Звук", "Громкость, устройства, приложения и микрофон. Значок в доке справа.", audioOn, on =>
        {
            _s.Config.Update(_ => _s.Config.Widget("audio").Enabled = on);
            Build();
            Layout();
        }));
        _body.Children.Add(SwitchRow("Сейчас играет", "Музыка и видео в верхней панели, пока играют.", _s.Config.WidgetEnabled("media"), on =>
            _s.Config.Update(_ => _s.Config.Widget("media").Enabled = on)));
        if (!audioOn) return;

        var a = _s.AudioSettings;
        var head = Text("Звук", 14, bold: true);
        head.Margin = new Thickness(0, 10, 0, 0);
        _body.Children.Add(head);

        // Шаг колеса над значком звука.
        var stepRow = new Grid { ColumnSpacing = 12, MinHeight = 44 };
        stepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        stepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        stepRow.Children.Add(Text("Шаг колеса мыши"));
        var value = Text($"{a.Step} %", 13, secondary: true);
        var slider = new Slider { Minimum = 1, Maximum = 10, StepFrequency = 1, Value = a.Step, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            value.Text = $"{(int)e.NewValue} %";
            _s.Config.Update(_ => _s.AudioSettings.Step = (int)e.NewValue);
        };
        Grid.SetColumn(slider, 1);
        stepRow.Children.Add(slider);
        Grid.SetColumn(value, 2);
        stepRow.Children.Add(value);
        _body.Children.Add(stepRow);

        _body.Children.Add(SwitchRow("Громкость приложений", "Свёрнутый список в окошке звука.", a.ShowApps, on => _s.Config.Update(_ => _s.AudioSettings.ShowApps = on)));
        _body.Children.Add(SwitchRow("Микрофон", "Ползунок микрофона в окошке звука.", a.ShowMic, on => _s.Config.Update(_ => _s.AudioSettings.ShowMic = on)));

        // Профили: устройство и громкость одним кликом или горячей клавишей.
        var profHead = Text("Профили", 13, secondary: true);
        profHead.Margin = new Thickness(0, 8, 0, 0);
        _body.Children.Add(profHead);
        var profiles = a.Profiles;
        foreach (var p in profiles)
        {
            var row = new Grid { ColumnSpacing = 8, MinHeight = 40 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Text(p.Name, 13));
            var profile = p;
            var hotkey = new HotkeyBox(p.Hotkey, k => _s.Config.Update(_ =>
            {
                var list = _s.AudioSettings.Profiles;
                var i = list.FindIndex(x => x.Id == profile.Id);
                if (i >= 0) { list[i] = list[i] with { Hotkey = k }; _s.AudioSettings.Profiles = list; }
            }));
            Grid.SetColumn(hotkey, 1);
            row.Children.Add(hotkey);
            var del = new Button
            {
                Content = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 13 },
                Width = 36,
                Height = 32,
                Padding = new Thickness(0),
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(del, "Удалить профиль");
            del.Click += (_, _) =>
            {
                _s.Config.Update(_ => _s.AudioSettings.Profiles = _s.AudioSettings.Profiles.Where(x => x.Id != profile.Id).ToList());
                Build();
                Layout();
            };
            Grid.SetColumn(del, 2);
            row.Children.Add(del);
            _body.Children.Add(row);
        }
        var devices = _s.Audio.Devices();
        var current = devices.FirstOrDefault(d => d.Id == _s.Audio.DefaultId());
        if (current != null)
        {
            var name = $"{AudioSettings.ShortName(current.Name, devices.Select(d => d.Name))} {_s.Audio.Volume}%";
            var add = new Button { Content = Text($"＋ Добавить «{name}»", 13), Margin = new Thickness(0, 2, 0, 0) };
            add.Click += (_, _) =>
            {
                _s.Config.Update(_ =>
                {
                    var list = _s.AudioSettings.Profiles;
                    var n = list.Count + 1;
                    while (list.Any(x => x.Id == $"profile-{n}")) n++;
                    list.Add(new AudioProfile($"profile-{n}", name, current.Id, _s.Audio.Volume, null));
                    _s.AudioSettings.Profiles = list;
                });
                Build();
                Layout();
            };
            _body.Children.Add(add);
        }

        var nextRow = new Grid { ColumnSpacing = 8, MinHeight = 44, Margin = new Thickness(0, 8, 0, 0) };
        nextRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nextRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        nextRow.Children.Add(Text("Следующее устройство вывода", 13));
        var next = new HotkeyBox(a.NextOutputHotkey, k => _s.Config.Update(_ => _s.AudioSettings.NextOutputHotkey = k));
        Grid.SetColumn(next, 1);
        nextRow.Children.Add(next);
        _body.Children.Add(nextRow);
    }
}

/// Поле горячей клавиши: клик — «Нажмите сочетание…», нажал — записалось. Esc — отмена,
/// Backspace или Delete — убрать сочетание.
public sealed class HotkeyBox : Button
{
    readonly TextBlock _text = new() { FontFamily = Theme.Font, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Action<string?> _changed;
    string? _value;
    bool _capturing;

    public HotkeyBox(string? value, Action<string?> changed)
    {
        _value = value;
        _changed = changed;
        Content = _text;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Height = 32;
        Show();
        Click += (_, _) => { _capturing = true; Show(); Focus(FocusState.Programmatic); };
        LostFocus += (_, _) => { _capturing = false; Show(); };
        KeyDown += OnKey;
    }

    void Show()
    {
        _text.Text = _capturing ? "Нажмите сочетание…" : _value ?? "Не задано";
        _text.Foreground = Theme.Brush(_capturing || _value is null ? Theme.TextSecondary : Theme.Text);
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                _capturing = false;
                Show();
                return;
            case VirtualKey.Back or VirtualKey.Delete:
                _capturing = false;
                _value = null;
                Show();
                _changed(null);
                return;
            case VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu:
                return;
        }
        var hotkey = Hotkeys.Format(Win32.KeyDown(VK_CONTROL), Win32.KeyDown(0x12), Win32.KeyDown(VK_SHIFT), Win32.KeyDown(VK_LWIN) || Win32.KeyDown(0x5C), (uint)e.Key);
        if (hotkey is null) return;
        _capturing = false;
        _value = hotkey;
        Show();
        _changed(hotkey);
    }
}
