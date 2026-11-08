using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using WindowsShell.Apps;
using WindowsShell.Tail;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island.Widgets;

/// Bluetooth внутри виджета сети (И4, WSH-163): переключатель, свои устройства (подключено или
/// нет; наушники — «Подключить/Отключить» по кнопке; «Удалить» с подтверждением), «Добавить
/// устройство» — поиск и сопряжение с PIN прямо в острове. Заряд BT-устройств — в виджете заряда.
public sealed class BtPane
{
    static readonly Color DangerBg = Color.FromArgb(255, 0x2A, 0x14, 0x14);

    readonly Services _s;
    readonly IslandBody _body;
    readonly Action _rebuild;
    readonly Dictionary<string, BtFound> _found = [];
    List<BtDevice>? _devices;
    Radio? _radio;
    DispatcherQueueTimer? _timer;
    IDisposable? _scan;
    int _version;
    bool _open;
    string? _busy, _message, _messageFor, _confirmRemove, _pairing;
    BtDevices.PairAsk? _ask;
    TaskCompletionSource<(bool, string?)>? _answer;
    string _pin = "";

    public BtPane(Services s, IslandBody body, Action rebuild)
    {
        _s = s;
        _body = body;
        _rebuild = rebuild;
    }

    /// Вводят PIN — не пересобирать (поле потеряло бы фокус).
    public bool Typing => _ask is { Kind: DevicePairingKinds.ProvidePin };

    public void Open()
    {
        if (_open) return;
        _open = true;
        _message = null;
        _confirmRemove = null;
        var v = ++_version;
        Reload(v);
        _ = LoadRadio(v);
        _timer ??= _s.Queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick -= OnTimer;
        _timer.Tick += OnTimer;
        _timer.Start();
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        _version++;
        _timer?.Stop();
        StopScan();
        _answer?.TrySetResult((false, null));
        _answer = null;
        _ask = null;
        _pin = "";
    }

    void OnTimer(DispatcherQueueTimer t, object o) => Reload(_version);

    void Reload(int v)
    {
        _ = Task.Run(() =>
        {
            List<BtDevice> list;
            try { list = BtDevices.List(); }
            catch (Exception e) { Log.Error("Bluetooth: список", e); list = []; }
            _s.Queue.TryEnqueue(() =>
            {
                if (v != _version || !_open) return;
                var changed = _devices is null || !list.Select(d => (d.Key, d.Connected, d.Name)).SequenceEqual(_devices.Select(d => (d.Key, d.Connected, d.Name)));
                _devices = list;
                if (changed && !Typing) _rebuild();
            });
        });
    }

    async Task LoadRadio(int v)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
            var bt = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            _s.Queue.TryEnqueue(() => { if (v == _version && _open) { _radio = bt; _rebuild(); } });
        }
        catch (Exception e) { Log.Error("Bluetooth: радио", e); }
    }

    public void Build(StackPanel root)
    {
        var head = new Grid { Margin = new Thickness(4, 0, 0, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text("Bluetooth", 18, bold: true));
        var on = _radio?.State != RadioState.Off;
        if (_radio != null)
        {
            var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "Bluetooth");
            toggle.Toggled += async (_, _) =>
            {
                try { await _radio.SetStateAsync(toggle.IsOn ? RadioState.On : RadioState.Off); }
                catch (Exception e) { Log.Error("Bluetooth: вкл/выкл", e); }
                if (!toggle.IsOn) StopScan();
                _rebuild();
            };
            Grid.SetColumn(toggle, 1);
            head.Children.Add(toggle);
        }
        root.Children.Add(head);

        if (_radio != null && !on)
        {
            root.Children.Add(Note("Bluetooth выключен."));
        }
        else
        {
            root.Children.Add(Caption("МОИ УСТРОЙСТВА"));
            var rows = new StackPanel { Spacing = 2 };
            if (_devices is null) rows.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, Margin = new Thickness(10) });
            else if (_devices.Count == 0) rows.Children.Add(new Border { Padding = new Thickness(12), Child = Text("Сопряжённых устройств нет. Добавь новое ниже.", 13, Sub, wrap: true) });
            else foreach (var d in _devices) rows.Children.Add(DeviceRow(d));
            root.Children.Add(GroupBox(rows));
            BuildAdd(root);
        }

        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(Note("Заряд Bluetooth-устройств — в виджете «Заряд»."));
        var link = Link("Параметры Bluetooth ↗", () => { _body.Sleep(restoreFocus: false); Launcher.Open("ms-settings:bluetooth"); });
        Grid.SetColumn(link, 1);
        foot.Children.Add(link);
        root.Children.Add(foot);
    }

    UIElement DeviceRow(BtDevice d)
    {
        var box = new StackPanel { Spacing = 6 };
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Glyph(d.Glyph, 18, d.Connected ? Ink : Sub));
        var lines = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = Text(d.Name, 14, d.Connected ? Ink : Sub, bold: d.Connected);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        lines.Children.Add(name);
        lines.Children.Add(Text(_busy == d.Key ? "…" : d.Connected ? "Подключено" : "Не подключено", 12, d.Connected ? Green : Faint));
        Grid.SetColumn(lines, 1);
        g.Children.Add(lines);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (d.Audio && d.AudioFilters.Count > 0)
        {
            var b = Btn(d.Connected ? "Отключить" : "Подключить", primary: !d.Connected, click: () => Toggle(d));
            b.IsEnabled = _busy is null;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"{(d.Connected ? "Отключить" : "Подключить")} {d.Name}");
            buttons.Children.Add(b);
        }
        var remove = Btn("Удалить", click: () => { _confirmRemove = d.Key; _message = null; _rebuild(); });
        remove.Opacity = 0.7;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Удалить {d.Name}");
        buttons.Children.Add(remove);
        Grid.SetColumn(buttons, 2);
        g.Children.Add(buttons);
        box.Children.Add(g);

        if (_confirmRemove == d.Key)
        {
            var c = new Grid { ColumnSpacing = 8, Padding = new Thickness(10), Background = B(DangerBg), CornerRadius = new CornerRadius(12) };
            c.ColumnDefinitions.Add(new ColumnDefinition());
            c.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            c.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            c.Children.Add(Text($"Удалить «{d.Name}»? Чтобы снова пользоваться, его придётся сопрячь заново.", 12, wrap: true));
            var cancel = Btn("Отмена", click: () => { _confirmRemove = null; _rebuild(); });
            Grid.SetColumn(cancel, 1);
            c.Children.Add(cancel);
            var del = Btn("Удалить", click: () => Remove(d));
            del.Background = B(Red);
            ((TextBlock)del.Content).Foreground = B(Color.FromArgb(255, 0x1A, 0x06, 0x05));
            Grid.SetColumn(del, 2);
            c.Children.Add(del);
            box.Children.Add(c);
            cancel.Loaded += (_, _) => cancel.Focus(FocusState.Programmatic);
        }
        if (_messageFor == d.Key && _message != null) box.Children.Add(Text(_message, 12, AmberText, wrap: true));
        return new Border { Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(12), Child = box };
    }

    void Toggle(BtDevice d)
    {
        _busy = d.Key;
        _message = null;
        _rebuild();
        var v = _version;
        _ = Task.Run(() =>
        {
            var error = BtDevices.SetConnected(d, !d.Connected);
            // Windows подключает наушники не сразу: несколько секунд даём ей.
            if (error is null) Thread.Sleep(2500);
            _s.Queue.TryEnqueue(() =>
            {
                _busy = null;
                if (v != _version || !_open) return;
                _message = error;
                _messageFor = d.Key;
                Reload(v);
                _rebuild();
            });
        });
    }

    async void Remove(BtDevice d)
    {
        _confirmRemove = null;
        _busy = d.Key;
        _rebuild();
        var error = await BtDevices.Remove(d);
        _busy = null;
        if (!_open) return;
        _message = error;
        _messageFor = d.Key;
        Reload(_version);
        _rebuild();
    }

    // ---------- новое устройство ----------

    void BuildAdd(StackPanel root)
    {
        if (_scan is null && _pairing is null)
        {
            var add = Btn("+ Добавить устройство", click: StartScan);
            root.Children.Add(add);
            if (_message != null && _messageFor == "pair") root.Children.Add(Text(_message, 12, _message.StartsWith("Сопряжено") ? Green : AmberText, wrap: true));
            return;
        }
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Caption("ДОБАВИТЬ УСТРОЙСТВО"));
        var stop = Link("Готово", () => { StopScan(); _rebuild(); });
        Grid.SetColumn(stop, 1);
        head.Children.Add(stop);
        root.Children.Add(head);

        if (_pairing != null)
        {
            root.Children.Add(GroupBox(new Border { Padding = new Thickness(12), Child = PairView() }));
            return;
        }
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(Text("Ищу рядом… Переведи устройство в режим сопряжения (обычно — долго держать кнопку питания или Bluetooth).", 12, Sub, wrap: true));
        foreach (var f in _found.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Take(12))
        {
            var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 6, 10, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Text(f.Name, 14);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(name);
            var pair = Btn("Сопрячь", primary: true, click: () => Pair(f));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pair, $"Сопрячь {f.Name}");
            Grid.SetColumn(pair, 1);
            g.Children.Add(pair);
            list.Children.Add(g);
        }
        if (_found.Count == 0) list.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 4, 0, 4) });
        root.Children.Add(GroupBox(new Border { Padding = new Thickness(6), Child = list }));
    }

    UIElement PairView()
    {
        var box = new StackPanel { Spacing = 10 };
        var name = _found.TryGetValue(_pairing!, out var f) ? f.Name : "устройство";
        if (_ask is null)
        {
            box.Children.Add(Text($"Сопрягаю «{name}»…", 14, bold: true));
            box.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
            return box;
        }
        switch (_ask.Kind)
        {
            case DevicePairingKinds.DisplayPin:
                box.Children.Add(Text($"Введи этот PIN на «{name}»:", 13, wrap: true));
                box.Children.Add(Text(_ask.Pin ?? "", 28, bold: true));
                box.Children.Add(Answers("Готово", ok: true));
                break;
            case DevicePairingKinds.ConfirmPinMatch:
                box.Children.Add(Text($"Совпадает ли PIN на «{name}»?", 13, wrap: true));
                box.Children.Add(Text(_ask.Pin ?? "", 28, bold: true));
                box.Children.Add(Answers("Совпадает", ok: true));
                break;
            default:
            {
                box.Children.Add(Text($"Введи PIN для «{name}» (часто 0000 или 1234, или указан в инструкции):", 13, wrap: true));
                var input = new TextBox { Text = _pin, MaxLength = 16, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 16 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "PIN устройства");
                input.TextChanged += (_, _) => _pin = input.Text;
                input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Answer(true, _pin); };
                input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
                box.Children.Add(input);
                box.Children.Add(Answers("Сопрячь", ok: true, pin: true));
                break;
            }
        }
        return box;
    }

    UIElement Answers(string yes, bool ok, bool pin = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Btn(yes, primary: true, click: () => Answer(ok, pin ? _pin : null)));
        row.Children.Add(Btn("Отмена", click: () => Answer(false, null)));
        return row;
    }

    void Answer(bool ok, string? pin)
    {
        var a = _answer;
        _answer = null;
        _ask = null;
        _pin = "";
        a?.TrySetResult((ok, pin));
        _rebuild();
    }

    void StartScan()
    {
        if (_scan != null) return;
        _found.Clear();
        _message = null;
        var v = _version;
        _scan = BtDevices.Scan(
            f => _s.Queue.TryEnqueue(() => { if (v != _version || !_open) return; _found[f.Id] = f; if (_pairing is null) _rebuild(); }),
            id => _s.Queue.TryEnqueue(() => { if (v != _version || !_open) return; if (_found.Remove(id) && _pairing is null) _rebuild(); }));
        _rebuild();
    }

    void StopScan()
    {
        _scan?.Dispose();
        _scan = null;
        _found.Clear();
    }

    async void Pair(BtFound f)
    {
        _pairing = f.Id;
        _message = null;
        _rebuild();
        var error = await BtDevices.Pair(f.Id, ask =>
        {
            var tcs = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _s.Queue.TryEnqueue(() =>
            {
                if (!_open) { tcs.TrySetResult((false, null)); return; }
                _answer?.TrySetResult((false, null));
                _answer = tcs;
                _ask = ask;
                _pin = "";
                _rebuild();
            });
            return tcs.Task;
        });
        _s.Queue.TryEnqueue(() =>
        {
            _pairing = null;
            _ask = null;
            _answer = null;
            if (!_open) return;
            StopScan();
            _message = error ?? $"Сопряжено: «{f.Name}».";
            _messageFor = "pair";
            Reload(_version);
            _rebuild();
        });
    }

    static TextBlock Caption(string text)
    {
        var t = Text(text, 13, Sub, bold: true);
        t.Margin = new Thickness(4, 4, 0, 0);
        return t;
    }

    static TextBlock Note(string text)
    {
        var t = Text(text, 12, Faint, wrap: true);
        t.Margin = new Thickness(4, 0, 4, 0);
        return t;
    }
}
