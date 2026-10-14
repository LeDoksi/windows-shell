using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Apps;
using WindowsShell.Native;
using WindowsShell.Tail;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Dock;

/// Системный хвост дока (раздел 2, WSH-E9): ⌃ трей · сеть · звук · батарея · язык · уведомления ·
/// часы. Полный хвост — на мониторе хвоста, на остальных только часы (раздел 9).
public sealed partial class Dock
{
    /// Значок хвоста: место в доке, вид и показан ли он сейчас.
    sealed class Slot
    {
        public required string Id;
        public required Grid Visual;
        public required double W;
        public bool Shown;
        public double X;
    }

    readonly List<Slot> _slots = [];
    Slot _traySlot = null!, _netSlot = null!, _soundSlot = null!, _batSlot = null!, _langSlot = null!, _bellSlot = null!, _clockSlot = null!;
    FontIcon _soundIcon = null!, _netIcon = null!, _batIcon = null!, _bellIcon = null!;
    Border _trayDot = null!, _bellDot = null!;
    TextBlock _lang = null!, _time = null!, _date = null!;

    void CreateTail()
    {
        Slot Add(string id, double w, params UIElement[] children)
        {
            var g = new Grid
            {
                Width = w,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                // Скрыт, пока UpdateTail его не покажет: иначе выключенный значок (батарея на ПК
                // без батареи) стоит видимым в левом углу полосы и вылезает, когда док под курсором.
                Visibility = Visibility.Collapsed,
            };
            foreach (var c in children) g.Children.Add(c);
            _canvas.Children.Add(g);
            var slot = new Slot { Id = id, Visual = g, W = w };
            _slots.Add(slot);
            return slot;
        }
        FontIcon Glyph(string glyph, double size = 16) => new() { Glyph = glyph, FontFamily = Theme.Icons, FontSize = size, Foreground = Theme.Brush(Theme.Text) };
        Border Dot() => new()
        {
            Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            Background = Theme.Brush(Theme.Warning),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 7, 0),
            Visibility = Visibility.Collapsed,
        };

        _trayDot = Dot();
        _traySlot = Add("tray", 30, Glyph("", 12), _trayDot);
        _netIcon = Glyph("");
        _netSlot = Add("network", 36, _netIcon);
        _soundIcon = Glyph("", 18);
        _soundSlot = Add("sound", 36, _soundIcon);
        _batIcon = Glyph("", 18);
        _batSlot = Add("battery", 36, _batIcon);
        _lang = new TextBlock { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.Text), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _langSlot = Add("language", 32, _lang);
        _bellIcon = Glyph("", 15);
        _bellDot = Dot();
        _bellSlot = Add("notifications", 32, _bellIcon, _bellDot);
        _time = new TextBlock { FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), HorizontalAlignment = HorizontalAlignment.Right };
        _date = new TextBlock { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextSecondary), HorizontalAlignment = HorizontalAlignment.Right };
        _clockSlot = Add("clock", 74, new StackPanel { Padding = new Thickness(0, 2, 6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _time, _date } });
    }

    void SubscribeTail()
    {
        _s.Audio.Changed += OnTailChanged;
        _s.Network.Changed += OnTailChanged;
        _s.Battery.Changed += OnTailChanged;
        _s.Language.Changed += OnTailChanged;
        _s.Notifications.Changed += OnTailChanged;
        _s.Tray.Changed += OnTailChanged;
    }

    void UnsubscribeTail()
    {
        _s.Audio.Changed -= OnTailChanged;
        _s.Network.Changed -= OnTailChanged;
        _s.Battery.Changed -= OnTailChanged;
        _s.Language.Changed -= OnTailChanged;
        _s.Notifications.Changed -= OnTailChanged;
        _s.Tray.Changed -= OnTailChanged;
    }

    void OnTailChanged()
    {
        if (UpdateTail()) { Relayout(); Kick(); }
    }

    /// Обновить значки хвоста. true — набор значков поменялся (нужна новая раскладка).
    public bool UpdateTail()
    {
        var t = _s.Config.Tail;
        var full = _fullTail;
        var changed = false;
        void Show(Slot s, bool on)
        {
            if (s.Shown == on) return;
            s.Shown = on;
            s.Visual.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            changed = true;
        }
        Show(_traySlot, full && t.Tray && _s.Tray.Running);
        Show(_netSlot, full && t.Network);
        Show(_soundSlot, full && _s.Config.WidgetEnabled("audio"));
        Show(_batSlot, full && t.Battery && _s.Battery.State.Present);
        Show(_langSlot, full && t.Language);
        Show(_bellSlot, full && t.Notifications);
        Show(_clockSlot, true);

        _trayDot.Visibility = _s.TrayAttention ? Visibility.Visible : Visibility.Collapsed;
        _netIcon.Glyph = _s.Network.State.Glyph;
        var v = _s.Audio.Volume;
        _soundIcon.Glyph = !_s.Audio.HasOutput ? "" : _s.Audio.Muted ? "" : v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
        if (Equals(_tooltipFor, "volume")) { _tooltipText.Text = $"{v} %"; PlaceTooltip(); }
        var bat = _s.Battery.State;
        _batIcon.Glyph = bat.Glyph;
        _batIcon.Foreground = Theme.Brush(bat.Color);
        _lang.Text = _s.Language.Current.Short;
        var n = _s.Notifications;
        _bellIcon.Glyph = n.DoNotDisturb ? "" : "";
        _bellDot.Visibility = n.Unread && !n.DoNotDisturb ? Visibility.Visible : Visibility.Collapsed;
        return changed;
    }

    void UpdateSound() => UpdateTail();

    public void UpdateClock()
    {
        var now = DateTime.Now;
        _time.Text = now.ToString("HH:mm");
        _date.Text = now.ToString("dd.MM.yyyy");
        _clockTimer.Interval = TimeSpan.FromSeconds(60 - now.Second + 0.05);
        _clockTimer.Start();
    }

    void HighlightSlot(Slot? hot)
    {
        foreach (var s in _slots) s.Visual.Background = Theme.Brush(s == hot ? Theme.Hover : Microsoft.UI.Colors.Transparent);
    }

    string SlotTooltip(Slot t)
    {
        switch (t.Id)
        {
            case "tray": return "Фоновые приложения";
            case "network":
            {
                var n = _s.Network.State;
                return n.Kind == NetKind.None ? n.Status : $"{(string.IsNullOrEmpty(n.Name) ? "Сеть" : n.Name)}\n{n.Status}";
            }
            case "sound": return _s.Audio.Muted ? "Звук выключен" : $"Громкость {_s.Audio.Volume} %";
            case "battery":
            {
                var b = _s.Battery.State;
                return $"Батарея {b.Percent} %{(b.Charging ? ", заряжается" : b.Plugged ? ", от сети" : "")}";
            }
            case "language": return _s.Language.Current.Name;
            case "notifications": return _s.Notifications.DoNotDisturb ? "Не беспокоить" : "Уведомления";
            default:
            {
                var d = DateTime.Now.ToString("dddd, d MMMM yyyy");
                return d.Length > 0 ? char.ToUpper(d[0]) + d[1..] : d;
            }
        }
    }

    void SlotClick(Slot t)
    {
        var key = ("tail", this, t.Id);
        switch (t.Id)
        {
            case "tray":
                _s.Tray.Anchor = AnchorRect(t);
                if (_s.TrayAttention) { _s.TrayAttention = false; UpdateTail(); }
                Popups.Toggle(key, () => _s.TrayFlyout.Show(key, AnchorPx(t), this));
                break;
            case "network":
                Popups.Toggle(key, () => _s.NetworkFlyout.Show(key, AnchorPx(t), this));
                break;
            case "sound":
                Popups.Toggle(key, () => _s.SoundFlyout.Show(key, AnchorPx(t), this));
                break;
            case "battery":
                Popups.Toggle(key, () => _s.BatteryFlyout.Show(key, AnchorPx(t), this));
                break;
            case "language":
            {
                // Раскладка меняется в окне, в котором работаешь, а не в нашем меню.
                var target = _s.Tracker.Foreground;
                var current = _s.Language.Current.Hkl;
                var items = InputLanguage.All().Select(l => new MenuItem
                {
                    Text = $"{l.Short}  {l.Name}",
                    Checked = l.Hkl == current,
                    Invoke = () => _s.Language.Select(target, l),
                }).ToList();
                items.Add(MenuItem.Sep);
                items.Add(new MenuItem { Text = "Параметры языка", Glyph = "", Invoke = () => Launcher.Open("ms-settings:regionlanguage") });
                Popups.Toggle(key, () => _s.Menu.Show(key, AnchorPx(t), items, owner: this));
                break;
            }
            case "notifications":
                Popups.CloseAll();
                _s.Notifications.MarkRead();
                // Центр уведомлений Windows, как Win+N.
                WinKeys.SendWinCombo('N');
                break;
            default:
                Popups.Toggle(key, () => _s.ClockFlyout.Show(key, AnchorPx(t), this));
                break;
        }
    }

    void SlotRightClick(Slot t)
    {
        var key = ("menu", this, t.Id);
        List<MenuItem>? items = t.Id switch
        {
            "sound" => SoundMenu(),
            "network" =>
            [
                new() { Text = "Параметры сети", Glyph = "", Invoke = () => Launcher.Open("ms-settings:network") },
                new() { Text = "Диагностика неполадок", Glyph = "", Invoke = () => Launcher.Open("ms-settings:troubleshoot") },
                new() { Text = "Сетевые подключения", Glyph = "", Invoke = () => Launcher.Run("control.exe", args: "ncpa.cpl") },
            ],
            "battery" => [new() { Text = "Параметры питания и батареи", Glyph = "", Invoke = () => Launcher.Open("ms-settings:batterysaver") }],
            "language" => [new() { Text = "Параметры языка", Glyph = "", Invoke = () => Launcher.Open("ms-settings:regionlanguage") }],
            "notifications" => [new() { Text = "Параметры уведомлений", Glyph = "", Invoke = () => Launcher.Open("ms-settings:notifications") }],
            "clock" => [new() { Text = "Настроить дату и время", Glyph = "", Invoke = () => Launcher.Open("ms-settings:dateandtime") }],
            _ => null,
        };
        if (t.Id == "tray") { SlotClick(t); return; }
        if (items is null) return;
        Popups.Toggle(key, () => _s.Menu.Show(key, AnchorPx(t), items, owner: this));
    }

    /// Место значка на экране (для Shell_NotifyIconGetRect).
    RECT AnchorRect(Slot t)
    {
        var r = AnchorPx(t);
        return new RECT { Left = (int)r.X, Top = (int)r.Y, Right = (int)(r.X + r.Width), Bottom = (int)(r.Y + r.Height) };
    }
}
