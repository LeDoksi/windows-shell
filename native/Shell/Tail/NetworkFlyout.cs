using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Radios;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Ui;
using static WindowsShell.Tail.FlyoutUi;

namespace WindowsShell.Tail;

/// Окошко сети (раздел 2.2, WSH-48): текущая сеть и статус, переключатель Wi-Fi, список сетей
/// Wi-Fi, «Параметры сети». Клик по знакомой сети подключает (крутится индикатор, потом
/// «Подключено»), по новой — открывает Параметры Windows → Wi-Fi (свой ввод пароля пока не делаем).
public sealed class NetworkFlyout : Popup
{
    readonly NetworkService _net;
    readonly StackPanel _body = new() { Padding = new Thickness(16, 14, 16, 10), Spacing = 2 };
    readonly DispatcherQueue _queue;
    Wlan.Result? _wifi;
    Radio? _radio;
    string? _connecting;
    long _connectUntil;
    readonly DispatcherQueueTimer _poll;
    int _version;

    public NetworkFlyout(NetworkService net) : base(activating: true)
    {
        _net = net;
        _queue = DispatcherQueue.GetForCurrentThread();
        SetContent(_body);
        _net.Changed += () => { if (IsOpen) { Build(); Layout(); } };
        _poll = _queue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(700);
        _poll.Tick += (_, _) => PollConnect();
    }

    protected override double Width => 360;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(width, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    public void Show(object anchor, Rect anchorPx, object? owner)
    {
        _connecting = null;
        Build();
        Open(anchor, anchorPx, owner: owner);
        var v = ++_version;
        // Сначала то, что Windows уже знает, потом свежий поиск сетей — в фоне.
        _ = Task.Run(() =>
        {
            var quick = Wlan.List(scan: false);
            _queue.TryEnqueue(() => { if (v == _version && IsOpen) { _wifi = quick; Build(); Layout(); } });
            if (!quick.HasAdapter) return;
            Thread.Sleep(2500);
            var fresh = Wlan.List(scan: false);
            _queue.TryEnqueue(() => { if (v == _version && IsOpen) { _wifi = fresh; Build(); Layout(); } });
        });
        _ = Task.Run(() => { try { Wlan.List(scan: true); } catch { } });
        _ = LoadRadio(v);
    }

    async Task LoadRadio(int v)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return;
            var radios = await Radio.GetRadiosAsync();
            var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            _queue.TryEnqueue(() => { if (v == _version && IsOpen) { _radio = wifi; Build(); Layout(); } });
        }
        catch (Exception e) { Log.Error("сеть: радио", e); }
    }

    protected override void OnClosing()
    {
        _version++;
        _poll.Stop();
    }

    void Build()
    {
        _body.Children.Clear();
        var s = _net.State;
        var head = Row(s.Glyph, s.Kind == NetKind.None ? "Нет сети" : string.IsNullOrEmpty(s.Name) ? (s.Kind == NetKind.Ethernet ? "Ethernet" : "Сеть") : s.Name,
            s.Status, accentIcon: s.Kind != NetKind.None);
        head.IsHitTestVisible = false;
        _body.Children.Add(head);

        if (_radio != null || _wifi is { HasAdapter: true })
        {
            _body.Children.Add(Separator());
            var on = _radio?.State != RadioState.Off;
            var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            toggle.Toggled += async (_, _) =>
            {
                if (_radio is null) { Close(restoreFocus: false); Launcher.Open("ms-settings:network-wifi"); return; }
                try { await _radio.SetStateAsync(toggle.IsOn ? RadioState.On : RadioState.Off); }
                catch (Exception e) { Log.Error("сеть: Wi-Fi вкл/выкл", e); }
                Build();
                Layout();
            };
            var grid = new Grid { Height = 40, Margin = new Thickness(12, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Label("Wi-Fi"));
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
            _body.Children.Add(grid);

            if (on) BuildList();
        }

        _body.Children.Add(Link("Параметры сети", () => { Close(restoreFocus: false); Launcher.Open("ms-settings:network"); }));
    }

    void BuildList()
    {
        if (_wifi is null)
        {
            _body.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20, Margin = new Thickness(0, 8, 0, 8) });
            return;
        }
        if (_wifi.LocationDenied)
        {
            var note = Label("Windows показывает сети Wi-Fi, только если программам разрешено местоположение.", 12, secondary: true);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(12, 4, 12, 0);
            _body.Children.Add(note);
            _body.Children.Add(Link("Открыть настройки местоположения", () => { Close(restoreFocus: false); Launcher.Open("ms-settings:privacy-location"); }));
            return;
        }
        if (_wifi.Networks.Count == 0)
        {
            var none = Label("Сети не найдены", 13, secondary: true);
            none.Margin = new Thickness(12, 4, 0, 4);
            _body.Children.Add(none);
            return;
        }
        var list = new StackPanel { Spacing = 2 };
        foreach (var n in _wifi.Networks.Take(12))
        {
            var glyph = n.Signal switch { < 25 => "", < 50 => "", < 75 => "", _ => "" };
            var connecting = _connecting == n.Ssid;
            var subtitle = n.Connected ? "Подключено" : connecting ? "Подключение…" : n.Secured ? "Защищённая" : "Открытая";
            UIElement? right = null;
            if (connecting) right = new ProgressRing { IsActive = true, Width = 18, Height = 18 };
            else if (n.Connected)
            {
                var off = new Button { Content = Label("Отключить", 12), Padding = new Thickness(10, 4, 10, 5), VerticalAlignment = VerticalAlignment.Center };
                off.Click += (_, _) => { Wlan.Disconnect(); _wifi = null; Refresh(); };
                right = off;
            }
            else if (n.Secured) right = Icon("", 12);
            var row = Row(glyph, n.Ssid, subtitle, right, selected: n.Connected, accentIcon: n.Connected);
            var net = n;
            row.Click += (_, _) => Choose(net);
            list.Children.Add(row);
        }
        _body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    void Choose(WifiNetwork n)
    {
        if (n.Connected) return;
        if (n.Profile is null)
        {
            // Новая сеть: пароль вводится в Параметрах Windows.
            Close(restoreFocus: false);
            Launcher.Open("ms-settings:network-wifi");
            return;
        }
        if (!Wlan.Connect(n.Profile))
        {
            Close(restoreFocus: false);
            Launcher.Open("ms-settings:network-wifi");
            return;
        }
        _connecting = n.Ssid;
        _connectUntil = Environment.TickCount64 + 15_000;
        _poll.Start();
        Build();
        Layout();
    }

    void PollConnect()
    {
        if (!IsOpen || _connecting is null) { _poll.Stop(); return; }
        Refresh();
        if (_wifi?.Networks.Any(x => x.Ssid == _connecting && x.Connected) == true || Environment.TickCount64 > _connectUntil)
        {
            _connecting = null;
            _poll.Stop();
            Build();
            Layout();
        }
    }

    void Refresh()
    {
        var v = _version;
        _ = Task.Run(() =>
        {
            var r = Wlan.List(scan: false);
            _queue.TryEnqueue(() => { if (v == _version && IsOpen) { _wifi = r; _net.Refresh(); Build(); Layout(); } });
        });
    }
}
