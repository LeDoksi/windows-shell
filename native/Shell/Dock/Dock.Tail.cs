using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Native;
using WindowsShell.Shots;
using WindowsShell.Tail;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Dock;

/// Системный хвост дока (раздел 2, WSH-E9): [временные значки] · ⌃ трей · сеть · звук · батарея ·
/// язык · уведомления · часы. Полный хвост — на мониторе хвоста, на остальных только часы (раздел 9).
/// Временные значки (design-v2 п. 2) стоят в постоянном порядке: Сейчас играет · Снимки ·
/// Корзина · Заряд; видны, когда нужны, появляются и исчезают плавно. Корзина — п. 3, заряд — п. 7.
/// Канбан (п. 9) — постоянный значок перед ⌃: быстрая задача, на нём число задач в работе.
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
        /// Временный значок: место растёт от 0 до 1 пружиной.
        public bool Temp;
        public double Grow = 1, GrowV;
        /// Сколько места занимает сейчас (с промежутком после себя).
        public double Width => Temp ? Grow * (W + TailGap) : Shown ? W + TailGap : 0;
    }

    readonly List<Slot> _slots = [];
    Slot _mediaSlot = null!, _shotsSlot = null!, _binSlot = null!, _chargeSlot = null!, _kanbanSlot = null!, _traySlot = null!, _netSlot = null!, _soundSlot = null!, _batSlot = null!, _langSlot = null!, _bellSlot = null!, _clockSlot = null!;
    FontIcon _binIcon = null!, _chargeIcon = null!, _soundIcon = null!, _netIcon = null!, _batIcon = null!, _bellIcon = null!;
    Border _trayDot = null!, _bellDot = null!, _chargeDot = null!;
    TextBlock _lang = null!, _time = null!, _date = null!, _kanbanCount = null!;
    Border _kanbanBadge = null!;
    Image _mediaCover = null!;
    FontIcon _mediaNote = null!;
    FontIcon _shotsIcon = null!;
    StackPanel _shotsRec = null!;
    TextBlock _shotsTime = null!;
    Border _shotsDot = null!;

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

        // Временные — левее ⌃, в постоянном порядке.
        _mediaCover = new Image { Width = 22, Height = 22, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        _mediaNote = Glyph("\uEC4F", 16);
        _mediaSlot = Add("media", 36, new Grid
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { _mediaNote, _mediaCover },
        });
        _mediaSlot.Temp = true;
        _mediaSlot.Grow = 0;
        // «Снимки» (design-v2 п. 4): камера; во время записи — красная точка с таймером.
        _shotsIcon = Glyph("\uE722", 16);
        _shotsDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Brush(Windows.UI.Color.FromArgb(255, 0xFF, 0x5B, 0x5B)), VerticalAlignment = VerticalAlignment.Center };
        _shotsTime = new TextBlock { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center };
        _shotsRec = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed, Children = { _shotsDot, _shotsTime },
        };
        _shotsSlot = Add("shots", 36, _shotsIcon, _shotsRec);
        _shotsSlot.Temp = true;
        _shotsSlot.Grow = 0;
        _binIcon = Glyph("\uE74D", 17);
        _binSlot = Add("bin", 36, _binIcon);
        _binSlot.Temp = true;
        _binSlot.Grow = 0;
        // «Заряд» (п. 7): уровень самого севшего устройства, оранжевая точка — пора зарядить.
        _chargeIcon = Glyph("\uE850", 18);
        _chargeDot = Dot();
        _chargeSlot = Add("charge", 36, _chargeIcon, _chargeDot);
        _chargeSlot.Temp = true;
        _chargeSlot.Grow = 0;

        // Канбан: список с галочками, число «В работе» голубым кружком.
        _kanbanCount = new TextBlock { FontFamily = Theme.Font, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Theme.Brush(Windows.UI.Color.FromArgb(255, 0x06, 0x23, 0x3A)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _kanbanBadge = new Border
        {
            MinWidth = 15, Height = 15, CornerRadius = new CornerRadius(7.5), Padding = new Thickness(3, 0, 3, 1),
            Background = Theme.Brush(Theme.Accent),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 2, 0),
            Child = _kanbanCount,
            Visibility = Visibility.Collapsed,
        };
        _kanbanSlot = Add("kanban", 36, Glyph("\uE9D5", 17), _kanbanBadge);

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
        _s.Media.Changed += OnTailChanged;
        _s.Bin.Changed += OnTailChanged;
        _s.Shots.Changed += OnTailChanged;
        _s.Shots.Tick += OnShotsTick;
        _s.Charge.Changed += OnTailChanged;
        _s.Kanban.Changed += OnTailChanged;
    }

    void UnsubscribeTail()
    {
        _s.Audio.Changed -= OnTailChanged;
        _s.Network.Changed -= OnTailChanged;
        _s.Battery.Changed -= OnTailChanged;
        _s.Language.Changed -= OnTailChanged;
        _s.Notifications.Changed -= OnTailChanged;
        _s.Tray.Changed -= OnTailChanged;
        _s.Media.Changed -= OnTailChanged;
        _s.Bin.Changed -= OnTailChanged;
        _s.Shots.Changed -= OnTailChanged;
        _s.Shots.Tick -= OnShotsTick;
        _s.Charge.Changed -= OnTailChanged;
        _s.Kanban.Changed -= OnTailChanged;
    }

    void OnShotsTick()
    {
        _shotsTime.Text = ShotsService.Length(_s.Shots.RecordingTime);
        _shotsDot.Opacity = _s.Shots.RecordingPaused ? 0.35 : 1;
        if (Equals(_tooltipFor, _shotsSlot)) { _tooltipText.Text = SlotTooltip(_shotsSlot); PlaceTooltip(); }
    }

    /// Где значок «Снимки», когда док виден: миниатюра снимка всплывает над ним, даже если док
    /// спрятан. null — значка в этом доке нет.
    public Rect? ShotsAnchorPx()
    {
        if (!_fullTail || !_shotsSlot.Shown) return null;
        return new Rect(_stripX + _shotsSlot.X * _scale, _stripY + Headroom * _scale, _shotsSlot.W * _scale, PillH * _scale);
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
            // Временные значки прячет и показывает кадр анимации.
            if (!s.Temp) s.Visual.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            changed = true;
        }
        var media = _s.Media.Current;
        Show(_mediaSlot, full && _s.Config.TempWidgetVisible("media", media != null));
        var shots = _s.Shots;
        var recording = shots.Recording;
        Show(_shotsSlot, full && shots.Enabled && (shots.Settings.Icon || recording));
        var shotsW = recording ? 74 : 36;
        if (_shotsSlot.W != shotsW)
        {
            _shotsSlot.W = shotsW;
            _shotsSlot.Visual.Width = shotsW;
            changed = true;
        }
        _shotsIcon.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        _shotsRec.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording) OnShotsTick();
        // Корзина: пока не пустая и пока из Проводника тащат файлы (чтобы было куда бросить).
        Show(_binSlot, full && _s.Config.TempWidgetVisible("bin", !_s.Bin.Empty || _binDrag));
        // Заряд: пока у какого-то устройства не выше порога.
        var low = _s.Charge.Low;
        Show(_chargeSlot, full && _s.Config.TempWidgetVisible("charge", low.Count > 0));
        var weakest = low.FirstOrDefault() ?? _s.Charge.Watched.FirstOrDefault(d => d.Percent != null);
        _chargeIcon.Glyph = weakest?.Percent is { } wp ? Charge.ChargeDevice.LevelGlyph(wp, weakest.Charging) : "\uE83F";
        _chargeIcon.Foreground = Theme.Brush(low.Count > 0 ? Theme.Warning : Theme.Text);
        _chargeDot.Visibility = low.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Equals(_tooltipFor, _chargeSlot)) { _tooltipText.Text = SlotTooltip(_chargeSlot); PlaceTooltip(); }
        var kanban = _s.Kanban;
        Show(_kanbanSlot, full && kanban.Enabled);
        var doing = kanban.Api.SignedIn && kanban.Settings.Badge ? kanban.Doing : 0;
        _kanbanCount.Text = doing > 99 ? "99+" : doing.ToString();
        _kanbanBadge.Visibility = doing > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Equals(_tooltipFor, _kanbanSlot)) { _tooltipText.Text = SlotTooltip(_kanbanSlot); PlaceTooltip(); }
        Show(_traySlot, full && t.Tray && _s.Tray.Running);
        Show(_netSlot, full && t.Network);
        Show(_soundSlot, full && _s.Config.WidgetEnabled("audio"));
        Show(_batSlot, full && t.Battery && _s.Battery.State.Present);
        Show(_langSlot, full && t.Language);
        Show(_bellSlot, full && t.Notifications);
        Show(_clockSlot, true);

        _mediaCover.Source = media != null ? _s.Media.Cover : null;
        _mediaNote.Visibility = _mediaCover.Source is null ? Visibility.Visible : Visibility.Collapsed;
        _binIcon.Foreground = Theme.Brush(_s.Bin.Empty ? Theme.TextSecondary : Theme.Text);
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
            case "media":
            {
                var m = _s.Media.Current;
                if (m is null) return "Сейчас ничего не играет";
                var title = m.Artist.Length > 0 ? $"{m.Title} — {m.Artist}" : m.Title;
                return m.Playing ? title : $"{title}\nПауза";
            }
            case "bin": return _s.Bin.Empty ? "Корзина пуста" : $"Корзина: {_s.Bin.Summary}";
            case "shots":
                if (_s.Shots.Recording)
                    return $"{(_s.Shots.RecordingPaused ? "Запись на паузе" : "Идёт запись")} · {ShotsService.Length(_s.Shots.RecordingTime)}\nКлик — остановить";
                return "Снимки\nPrtSc — снимок, Shift+PrtSc — запись";
            case "charge":
            {
                var list = _s.Charge.Watched.Where(d => d.Connected && d.Percent != null).ToList();
                if (list.Count == 0) return "Заряд устройств";
                return "Заряд устройств\n" + string.Join("\n", list.Select(d => $"{d.Name}: {d.Percent} %{(d.Charging ? ", заряжается" : "")}"));
            }
            case "kanban":
            {
                var k = _s.Kanban;
                if (!k.Api.SignedIn) return "Канбан\nКлик — войти";
                return k.LoadedAt is null ? "Канбан" : $"Канбан · в работе {k.Doing}\nКлик — новая задача";
            }
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
            case "media":
                if (_s.Media.Current is null) return;
                Popups.Toggle(key, () => _s.MediaFlyout.Show(key, AnchorPx(t), this));
                break;
            case "bin":
                Popups.Toggle(key, () => _s.BinFlyout.Show(key, AnchorPx(t), this));
                break;
            case "shots":
                if (_s.Shots.Recording) { Popups.CloseAll(); _s.Shots.StopRecording(); return; }
                Popups.Toggle(key, () => _s.ShotsFlyout.Show(key, AnchorPx(t), this));
                break;
            case "charge":
                Popups.Toggle(key, () => _s.ChargeFlyout.Show(key, AnchorPx(t), this));
                break;
            case "kanban":
                Popups.Toggle(key, () => _s.KanbanFlyout.Show(key, AnchorPx(t), this));
                break;
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
            "media" => MediaMenu(),
            "bin" =>
            [
                new() { Text = "Открыть корзину", Glyph = "\uE838", Invoke = RecycleBin.Open },
                new() { Text = "Очистить корзину", Glyph = "\uE74D", Danger = true, Enabled = !_s.Bin.Empty, Invoke = () => _s.BinFlyout.Show(("tail", this, "bin"), AnchorPx(t), this, confirm: true) },
                MenuItem.Sep,
                new() { Text = "Настройки виджета", Glyph = "\uE713", Invoke = () => _s.OpenSettings("widgets") },
            ],
            "shots" =>
            [
                new() { Text = "Снимок", Glyph = "\uE722", Invoke = () => After(_s.Shots.Snap) },
                _s.Shots.Recording
                    ? new() { Text = "Остановить запись", Glyph = "\uE71A", Invoke = _s.Shots.StopRecording }
                    : new() { Text = "Запись экрана", Glyph = "\uE7C8", Invoke = () => After(_s.Shots.Record) },
                new() { Text = "Открыть папку", Glyph = "\uE838", Invoke = ShotsService.OpenFolder },
                MenuItem.Sep,
                new() { Text = "Убрать значок", Glyph = "\uE711", Invoke = () => _s.Config.Update(_ => _s.Shots.Settings.Icon = false) },
                new() { Text = "Настройки виджета", Glyph = "\uE713", Invoke = () => _s.OpenSettings("widgets") },
            ],
            "charge" =>
            [
                new() { Text = "Bluetooth и устройства", Glyph = "\uE702", Invoke = () => Launcher.Open("ms-settings:bluetooth") },
                MenuItem.Sep,
                new() { Text = "Настройки виджета", Glyph = "\uE713", Invoke = () => _s.OpenSettings("widgets") },
            ],
            "kanban" =>
            [
                new() { Text = "Новая задача", Glyph = "\uE710", Invoke = () => SlotClick(t) },
                new() { Text = "Открыть окно канбана", Glyph = "\uE9D5", Invoke = () => _s.Kanban.OpenWindow() },
                new() { Text = "Сайт канбана", Glyph = "\uE774", Invoke = _s.Kanban.OpenSite },
                MenuItem.Sep,
                new() { Text = "Настройки виджета", Glyph = "\uE713", Invoke = () => _s.OpenSettings("kanban") },
            ],
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

    /// Снимок или запись из меню: меню успевает исчезнуть и не попадает в кадр.
    void After(Action action) => DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
    {
        await Task.Delay(200);
        action();
    });

    /// Правый клик по «Сейчас играет»: управление и настройки виджета (п. 2).
    List<MenuItem> MediaMenu()
    {
        var m = _s.Media.Current;
        var items = new List<MenuItem>();
        if (m != null)
        {
            items.Add(new() { Text = m.Playing ? "Пауза" : "Играть", Glyph = m.Playing ? "\uE769" : "\uE768", Enabled = m.CanPlayPause, Invoke = () => _s.Media.PlayPause() });
            items.Add(new() { Text = "Следующий", Glyph = "\uE893", Enabled = m.CanNext, Invoke = () => _s.Media.Next() });
            items.Add(new() { Text = "Предыдущий", Glyph = "\uE892", Enabled = m.CanPrevious, Invoke = () => _s.Media.Previous() });
            items.Add(MenuItem.Sep);
        }
        items.Add(new() { Text = "Настройки виджета", Glyph = "\uE713", Invoke = () => _s.OpenSettings("widgets") });
        return items;
    }

    /// Место значка на экране (для Shell_NotifyIconGetRect).
    RECT AnchorRect(Slot t)
    {
        var r = AnchorPx(t);
        return new RECT { Left = (int)r.X, Top = (int)r.Y, Right = (int)(r.X + r.Width), Bottom = (int)(r.Y + r.Height) };
    }
}
