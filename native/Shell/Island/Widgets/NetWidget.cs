using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Radios;
using WindowsShell.Apps;
using WindowsShell.Island.Core;
using WindowsShell.Tail;
using WindowsShell.Ui;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island.Widgets;

/// Сеть в острове (П15, И5): текущее подключение со статусом без догадок («без интернета»,
/// «нет сети»), знакомые Wi-Fi сразу, незнакомые — в раскрывающейся секции; подключение
/// к новой открытой или WPA2/WPA3-Personal сети с паролем — прямо здесь. Неверный пароль:
/// форма остаётся с введённым, созданный профиль удаляется. Корпоративные и скрытые сети —
/// в Параметры Windows. Пароль нигде не логируется. Вкладка «Bluetooth» (волна 4) — `BtPane`.
public sealed class NetWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    // Последняя вкладка — на время работы оболочки.
    static string _tab = "wifi";
    BtPane? _bt;

    Wlan.Result? _wifi;
    Radio? _radio;
    int _version;
    bool _othersOpen;
    string? _connecting, _created, _formFor, _error, _errorFor;
    string _password = "";
    long _connectUntil;
    DispatcherQueueTimer? _poll;

    public override string Id => "net";
    public override double Width => 600;

    BtPane Bt => _bt ??= new BtPane(S, Body, () => { if (IsOpen && _tab == "bt") Rebuild(); });

    void SetTab(string tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        if (tab == "bt") Bt.Open();
        else _bt?.Close();
        Rebuild();
    }

    protected override void OnOpen()
    {
        if (_tab == "bt") Bt.Open();
        _connecting = null;
        _formFor = null;
        _error = null;
        S.Network.Changed += OnNet;
        _poll ??= S.Queue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(700);
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        var v = ++_version;
        // Сначала то, что Windows уже знает, потом свежий поиск сетей — в фоне.
        _ = Task.Run(() =>
        {
            var quick = Wlan.List(scan: false);
            S.Queue.TryEnqueue(() => { if (v == _version && IsOpen) { _wifi = quick; Soft(); } });
            if (!quick.HasAdapter) return;
            Thread.Sleep(2500);
            var fresh = Wlan.List(scan: false);
            S.Queue.TryEnqueue(() => { if (v == _version && IsOpen) { _wifi = fresh; Soft(); } });
        });
        _ = Task.Run(() => { try { Wlan.List(scan: true); } catch { } });
        _ = LoadRadio(v);
    }

    protected override void OnClose()
    {
        _bt?.Close();
        _version++;
        S.Network.Changed -= OnNet;
        _poll?.Stop();
        _password = "";
    }

    void OnNet() => S.Queue.TryEnqueue(() => { if (IsOpen) Soft(); });

    /// Пересобрать, но не под пальцами: пока вводят пароль, список не трогаем (поле не теряет фокус).
    void Soft()
    {
        if (_tab == "bt") return; // на вкладке Bluetooth Wi-Fi не перерисовываем
        if (_formFor != null && _connecting is null) return; // форма открыта — пересоберём при «Отмена»/подключении
        Rebuild();
    }

    async Task LoadRadio(int v)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
            var wifi = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            S.Queue.TryEnqueue(() => { if (v == _version && IsOpen) { _radio = wifi; Soft(); } });
        }
        catch (Exception e) { Log.Error("сеть: радио", e); }
    }

    static string SignalGlyph(int signal) => signal switch { < 25 => "", < 50 => "", < 75 => "", _ => "" };

    protected override void Build()
    {
        Root.Children.Add(Tabs());
        if (_tab == "bt")
        {
            Bt.Build(Root);
            return;
        }
        var st = S.Network.State;
        var hasWifi = _radio != null || _wifi is { HasAdapter: true };

        // Заголовок: «Wi-Fi» с переключателем или просто «Сеть».
        var head = new Grid { Margin = new Thickness(4, 0, 0, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(hasWifi ? "Wi-Fi" : "Сеть", 18, bold: true));
        var wifiOn = _radio?.State != RadioState.Off;
        if (_radio != null)
        {
            var toggle = new ToggleSwitch { IsOn = wifiOn, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, "Wi-Fi");
            toggle.Toggled += async (_, _) =>
            {
                try { await _radio.SetStateAsync(toggle.IsOn ? RadioState.On : RadioState.Off); }
                catch (Exception e) { Log.Error("сеть: Wi-Fi вкл/выкл", e); }
                Rebuild();
            };
            Grid.SetColumn(toggle, 1);
            head.Children.Add(toggle);
        }
        Root.Children.Add(head);

        // Текущее подключение — всегда явным текстом.
        var current = new Grid { ColumnSpacing = 12 };
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        current.Children.Add(Glyph(st.Glyph, 20, st.Kind == NetKind.None || !st.Internet ? AmberText : Ink));
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(Text(st.Kind == NetKind.None ? "Нет подключения" : string.IsNullOrEmpty(st.Name) ? (st.Kind == NetKind.Ethernet ? "Кабель (Ethernet)" : "Сеть") : st.Name, 14, bold: true));
        lines.Children.Add(Text(st.Kind == NetKind.None ? "Интернета нет: ни кабеля, ни Wi-Fi" : !st.Internet ? "Подключено · интернета нет" : "Подключено · интернет есть",
            12, st.Kind == NetKind.None || !st.Internet ? AmberText : Sub));
        Grid.SetColumn(lines, 1);
        current.Children.Add(lines);
        if (st.Kind == NetKind.Wifi)
        {
            var off = Btn("Отключить", click: () => { Wlan.Disconnect(); Refresh(); });
            Grid.SetColumn(off, 2);
            current.Children.Add(off);
        }
        Root.Children.Add(GroupBox(new Border { Padding = new Thickness(10, 8, 10, 8), Child = current }));

        if (hasWifi && wifiOn) BuildWifi();
        else if (_radio != null && !wifiOn) Root.Children.Add(Note("Wi-Fi выключен."));

        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(Note(hasWifi ? "Корпоративные и скрытые сети — в параметрах Windows." : "Wi-Fi-адаптера нет."));
        var link = Link("Параметры сети Windows ↗", () => { Body.Sleep(restoreFocus: false); Launcher.Open(hasWifi ? "ms-settings:network-wifi" : "ms-settings:network"); });
        Grid.SetColumn(link, 1);
        foot.Children.Add(link);
        Root.Children.Add(foot);
    }

    UIElement Tabs()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(4), Background = B(Group), CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (id, title) in new[] { ("wifi", "Wi-Fi и сеть"), ("bt", "Bluetooth") })
        {
            var on = _tab == id;
            var b = new Button
            {
                Content = Text(title, 13, on ? Ink : Sub, bold: on),
                Background = B(on ? SelectedSlot : Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 6, 14, 7),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"{title}{(on ? ", выбрано" : "")}");
            var key = id;
            b.Click += (_, _) => SetTab(key);
            row.Children.Add(b);
        }
        return row;
    }

    static TextBlock Note(string text)
    {
        var t = Text(text, 12, Faint, wrap: true);
        t.Margin = new Thickness(4, 0, 4, 0);
        return t;
    }

    void BuildWifi()
    {
        if (_wifi is null)
        {
            Root.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
            return;
        }
        if (_wifi.LocationDenied)
        {
            Root.Children.Add(Note("Windows показывает сети Wi-Fi, только если программам разрешено местоположение."));
            Root.Children.Add(Link("Открыть настройки местоположения ↗", () => { Body.Sleep(restoreFocus: false); Launcher.Open("ms-settings:privacy-location"); }));
            return;
        }
        var nets = _wifi.Networks.Where(n => !n.Connected).ToList();
        var known = nets.Where(n => n.Profile != null).ToList();
        var others = nets.Where(n => n.Profile is null).ToList();
        if (nets.Count == 0) { Root.Children.Add(Note("Других сетей рядом не видно.")); return; }

        if (known.Count > 0)
        {
            var list = new StackPanel { Spacing = 2 };
            foreach (var n in known.Take(8)) list.Children.Add(NetRow(n, "знакомая"));
            Root.Children.Add(GroupBox(list));
        }
        if (others.Count > 0)
        {
            var toggle = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Text($"Другие сети · {others.Count}", 13), Glyph(_othersOpen ? "" : "", 10, Sub) } },
                Background = B(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2),
            };
            toggle.Click += (_, _) => { _othersOpen = !_othersOpen; Rebuild(); };
            Root.Children.Add(toggle);
            if (_othersOpen || _formFor != null)
            {
                var list = new StackPanel { Spacing = 2 };
                foreach (var n in others.Take(12))
                {
                    if (n.Ssid == _formFor) list.Children.Add(Form(n));
                    else list.Children.Add(NetRow(n, n.Security switch
                    {
                        WifiSecurity.Open => "открытая",
                        WifiSecurity.Wpa3Personal => "WPA3 · защищена",
                        WifiSecurity.Enterprise => "корпоративная · в параметрах Windows",
                        _ => "WPA2 · защищена",
                    }));
                }
                Root.Children.Add(GroupBox(list));
            }
        }
    }

    Button NetRow(WifiNetwork n, string sub)
    {
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Glyph(SignalGlyph(n.Signal), 18, Color.FromArgb(255, 0xC9, 0xC9, 0xCF)));
        var name = Text(n.Ssid, 14);
        ToolTipService.SetToolTip(name, n.Ssid);
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        UIElement right = _connecting == n.Ssid ? new ProgressRing { IsActive = true, Width = 18, Height = 18 }
            : _errorFor == n.Ssid && _error != null ? Text(_error, 12, RedText) : Text(sub, 12, Sub);
        Grid.SetColumn((FrameworkElement)right, 2);
        g.Children.Add(right);
        var row = Row(g, name: $"{n.Ssid}, {sub}");
        row.Click += (_, _) => Choose(n);
        return row;
    }

    /// Форма пароля в строке сети (эскиз «Wi-Fi: новая сеть с паролем прямо в острове»).
    UIElement Form(WifiNetwork n)
    {
        var box = new StackPanel { Spacing = 10, Padding = new Thickness(10), Background = B(RowOn), CornerRadius = new CornerRadius(12) };
        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Glyph(SignalGlyph(n.Signal), 18));
        var name = Text(n.Ssid, 14, bold: true);
        Grid.SetColumn(name, 1);
        top.Children.Add(name);
        var kind = Text(n.Security == WifiSecurity.Wpa3Personal ? "WPA3 · защищена" : "WPA2 · защищена", 12, Sub);
        Grid.SetColumn(kind, 2);
        top.Children.Add(kind);
        box.Children.Add(top);

        var pwd = new PasswordBox { Password = _password, PlaceholderText = "Пароль сети", PasswordRevealMode = PasswordRevealMode.Peek, IsEnabled = _connecting is null };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pwd, $"Пароль сети {n.Ssid}");
        pwd.PasswordChanged += (_, _) => { _password = pwd.Password; };
        box.Children.Add(pwd);
        if (_errorFor == n.Ssid && _error != null) box.Children.Add(Text(_error, 13, RedText, wrap: true));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Btn("Отмена", click: () => { _formFor = null; _password = ""; _error = null; Rebuild(); }));
        if (_connecting == n.Ssid) buttons.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        else buttons.Children.Add(Btn("Подключиться", primary: true, click: () => ConnectNew(n)));
        box.Children.Add(buttons);
        pwd.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; ConnectNew(n); } };
        pwd.Loaded += (_, _) => pwd.Focus(FocusState.Programmatic);
        return box;
    }

    void Choose(WifiNetwork n)
    {
        if (_connecting != null) return;
        _error = null;
        if (n.Profile != null)
        {
            if (!Wlan.Connect(n.Profile)) { _error = "Не удалось начать подключение"; _errorFor = n.Ssid; Rebuild(); return; }
            StartWaiting(n.Ssid, created: false);
            return;
        }
        switch (n.Security)
        {
            case WifiSecurity.Enterprise:
                Body.Sleep(restoreFocus: false);
                Launcher.Open("ms-settings:network-wifi");
                return;
            case WifiSecurity.Open:
                _password = "";
                ConnectNew(n);
                return;
            default:
                _formFor = n.Ssid;
                _password = "";
                Rebuild();
                return;
        }
    }

    void ConnectNew(WifiNetwork n)
    {
        if (_connecting != null) return;
        if (!WifiProfile.PasswordOk(n.Security, _password))
        {
            _error = "Пароль Wi-Fi — от 8 до 63 символов";
            _errorFor = n.Ssid;
            Rebuild();
            return;
        }
        var err = Wlan.ConnectNew(n.Ssid, n.Security, n.Security == WifiSecurity.Open ? null : _password);
        if (err != null) { _error = err; _errorFor = n.Ssid; Rebuild(); return; }
        Log.Write($"сеть: подключаюсь к новой сети «{n.Ssid}» из острова");
        StartWaiting(n.Ssid, created: true);
    }

    void StartWaiting(string ssid, bool created)
    {
        _connecting = ssid;
        _created = created ? ssid : null;
        _error = null;
        _connectUntil = Environment.TickCount64 + 15_000;
        _poll?.Start();
        Rebuild();
    }

    void OnPoll(DispatcherQueueTimer t, object o)
    {
        if (!IsOpen || _connecting is null) { t.Stop(); return; }
        var ssid = _connecting;
        var v = _version;
        _ = Task.Run(() =>
        {
            var r = Wlan.List(scan: false);
            S.Queue.TryEnqueue(() =>
            {
                if (v != _version || !IsOpen || _connecting != ssid) return;
                _wifi = r;
                var ok = r.Networks.Any(x => x.Ssid == ssid && x.Connected);
                if (!ok && Environment.TickCount64 < _connectUntil) return;
                t.Stop();
                _connecting = null;
                if (ok)
                {
                    Log.Write($"сеть: подключено к «{ssid}»");
                    _formFor = null;
                    _password = "";
                    _created = null;
                }
                else
                {
                    Log.Write($"сеть: к «{ssid}» подключиться не вышло");
                    // Неверный пароль: созданный нами профиль не оставляем, введённое не стираем.
                    if (_created == ssid) Wlan.DeleteProfile(ssid);
                    _created = null;
                    _error = _formFor == ssid ? "Не удалось подключиться — проверь пароль" : "Не удалось подключиться";
                    _errorFor = ssid;
                }
                S.Network.Refresh();
                Rebuild();
            });
        });
    }

    void Refresh()
    {
        var v = _version;
        _ = Task.Run(() =>
        {
            var r = Wlan.List(scan: false);
            S.Queue.TryEnqueue(() => { if (v == _version && IsOpen) { _wifi = r; S.Network.Refresh(); Soft(); } });
        });
    }
}
