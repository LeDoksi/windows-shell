using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WindowsShell.Config;
using WindowsShell.Tail;
using WindowsShell.Ui;
using static WindowsShell.Island.IslandUi;

namespace WindowsShell.Island;

/// Лента острова — приборная панель (П20): значки живые. Заряд — полоска самого севшего
/// устройства (янтарная ниже порога), звук — полоска громкости (колесо меняет её, П3), сеть —
/// уровень сигнала или «нет интернета», раскладка — «RU», медиа на постоянном месте (В3),
/// без сессии приглушено. После разделителя — настройки.
public sealed class RibbonView
{
    public const double Height = 62;

    sealed class Slot
    {
        public required string Id;
        public required Button Button;
        public required FontIcon Icon;
        public required TextBlock Text;
        public required Grid Bar;
        public required Border Fill;
        public long PressedAt;
    }

    readonly Services _s;
    readonly Action<string> _click;
    readonly Dictionary<string, Slot> _slots = [];
    readonly DispatcherQueueTimer _wheelTimer;
    string? _selected;
    string _key = "";
    bool _wheelShown;

    public StackPanel Panel { get; } = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        Padding = new Thickness(10, 7, 10, 7),
        Spacing = 2,
        Height = Height,
        XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled,
        TabFocusNavigation = KeyboardNavigationMode.Once,
    };

    public RibbonView(Services s, Action<string> click)
    {
        _s = s;
        _click = click;
        _wheelTimer = s.Queue.CreateTimer();
        _wheelTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _wheelTimer.IsRepeating = false;
        _wheelTimer.Tick += (_, _) => { _wheelShown = false; Refresh(); };
    }

    /// Ширина ленты, DIP.
    public double Width
    {
        get
        {
            Panel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, Height));
            return Math.Max(200, Panel.DesiredSize.Width);
        }
    }

    /// Состав ленты; `extra` — виджет, открытый событием, хотя его значок скрыт.
    public void Build(IReadOnlyList<string> ids, string? extra)
    {
        var all = ids.ToList();
        if (extra != null && extra != "settings" && !all.Contains(extra)) all.Add(extra);
        var key = string.Join(",", all);
        if (key == _key) { Refresh(); return; }
        _key = key;
        Panel.Children.Clear();
        _slots.Clear();
        foreach (var id in all) Panel.Children.Add(Make(id).Button);
        Panel.Children.Add(new Border { Width = 1, Height = 26, Background = B(Track), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        Panel.Children.Add(Make("settings").Button);
        Refresh();
    }

    public void Select(string? id)
    {
        _selected = id;
        foreach (var s in _slots.Values) s.Button.Background = B(s.Id == id ? SelectedSlot : Microsoft.UI.Colors.Transparent);
    }

    /// Фокус с клавиатуры на выбранный значок (стрелки — по ленте, Tab — в виджет).
    public void FocusSelected()
    {
        if (_selected != null && _slots.TryGetValue(_selected, out var s)) s.Button.Focus(FocusState.Programmatic);
    }

    Slot Make(string id)
    {
        var icon = Glyph("", 20);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var text = Text("", 14, bold: true);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.Visibility = Visibility.Collapsed;
        var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5), Background = B(Ink) };
        var bar = new Grid
        {
            Height = 3, Width = 22, CornerRadius = new CornerRadius(1.5), Background = B(Track),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed,
            Children = { fill },
        };
        var grid = new Grid { Width = 44, Height = 48, Children = { icon, text, bar } };
        var b = new Button
        {
            Content = grid,
            Width = 44,
            Height = 48,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Background = B(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        var slot = new Slot { Id = id, Button = b, Icon = icon, Text = text, Bar = bar, Fill = fill };
        // Отклик по нажатию, а не по отпусканию (принцип motion 1); Click — для клавиатуры и диктора.
        b.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
            slot.PressedAt = Environment.TickCount64;
            _click(id);
        }), true);
        b.Click += (_, _) => { if (Environment.TickCount64 - slot.PressedAt > 700) _click(id); };
        if (id == "sound") b.PointerWheelChanged += (_, e) =>
        {
            var delta = e.GetCurrentPoint(b).Properties.MouseWheelDelta;
            var step = _s.AudioSettings.Step;
            _s.Audio.Volume = Math.Clamp(_s.Audio.Volume + (delta > 0 ? step : -step), 0, 100);
            _wheelShown = true;
            _wheelTimer.Stop();
            _wheelTimer.Start();
            Refresh();
            e.Handled = true;
        };
        _slots[id] = slot;
        return slot;
    }

    /// Обновить индикаторы (службы что-то сообщили).
    public void Refresh()
    {
        foreach (var s in _slots.Values)
        {
            try { Paint(s); }
            catch (Exception e) { Log.Error($"остров: значок {s.Id}", e); }
        }
        Select(_selected);
    }

    void Paint(Slot s)
    {
        string tip;
        s.Button.Opacity = 1;
        s.Icon.Visibility = Visibility.Visible;
        s.Text.Visibility = Visibility.Collapsed;
        s.Bar.Visibility = Visibility.Collapsed;
        s.Icon.Foreground = B(Ink);
        switch (s.Id)
        {
            case "media":
            {
                var m = _s.Media.Current;
                s.Icon.Glyph = "";
                s.Button.Opacity = m is null ? 0.35 : 1;
                tip = m is null ? "Медиа: сейчас ничего не играет" : $"{(m.Playing ? "Играет" : "Пауза")}: {m.Title}{(m.Artist.Length > 0 ? " — " + m.Artist : "")}";
                break;
            }
            case "charge":
            {
                var c = _s.Charge;
                var low = c.Low;
                var weakest = low.FirstOrDefault() ?? c.Watched.Where(d => d.Connected && d.Percent != null).OrderBy(d => d.Percent).FirstOrDefault();
                s.Icon.Glyph = weakest?.Percent is { } p ? Charge.ChargeDevice.LevelGlyph(p, weakest.Charging) : "";
                if (weakest?.Percent is { } wp)
                {
                    s.Bar.Visibility = Visibility.Visible;
                    s.Fill.Width = 22 * wp / 100.0;
                    s.Fill.Background = B(low.Count > 0 ? Amber : Ink);
                }
                if (low.Count > 0) s.Icon.Foreground = B(Amber);
                tip = weakest is null ? "Заряд устройств" : $"Заряд: {weakest.Name} {weakest.Percent} %{(low.Count > 0 ? " — пора зарядить" : "")}";
                break;
            }
            case "net":
            {
                var n = _s.Network.State;
                s.Icon.Glyph = n.Glyph;
                if (n.Kind == NetKind.None || !n.Internet) s.Icon.Foreground = B(AmberText);
                tip = n.Kind == NetKind.None ? n.Status : $"{(string.IsNullOrEmpty(n.Name) ? "Сеть" : n.Name)} — {n.Status.ToLowerInvariant()}";
                break;
            }
            case "sound":
            {
                var a = _s.Audio;
                var v = a.Volume;
                s.Icon.Glyph = !a.HasOutput ? "" : a.Muted ? "" : v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
                s.Bar.Visibility = Visibility.Visible;
                s.Fill.Width = a.Muted ? 0 : 22 * v / 100.0;
                if (_wheelShown)
                {
                    s.Icon.Visibility = Visibility.Collapsed;
                    s.Text.Visibility = Visibility.Visible;
                    s.Text.Text = a.Muted ? "—" : v.ToString();
                }
                tip = a.Muted ? "Звук выключен" : $"Громкость {v} % (колесо — изменить)";
                break;
            }
            case "lang":
                s.Icon.Visibility = Visibility.Collapsed;
                s.Text.Visibility = Visibility.Visible;
                s.Text.Text = _s.Language.Current.Short;
                tip = $"Раскладка: {_s.Language.Current.Name}";
                break;
            default:
                s.Icon.Glyph = "";
                tip = "Настройки острова";
                break;
        }
        AutomationProperties.SetName(s.Button, tip);
        ToolTipService.SetToolTip(s.Button, tip);
    }
}
