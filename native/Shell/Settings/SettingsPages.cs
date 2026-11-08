using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Config;
using WindowsShell.Native;
using WindowsShell.Perf;
using WindowsShell.Ui;
using static WindowsShell.Settings.Rows;

namespace WindowsShell.Settings;

/// Страницы настроек (WSH-39, WSH-E23, WSH-E24): одно и то же строится и в окне настроек,
/// и в листе настроек острова (П14). Изменения применяются сразу через `change`, страница
/// перестраивается через `rebuild`.
public sealed class SettingsPages(Services s, Panel body, Action<Action<ConfigData>> change, Action rebuild)
{
    readonly Services _s = s;
    readonly Action _rebuild = rebuild;

    void Add(UIElement e) => body.Children.Add(e);

    void Change(Action<ConfigData> c) => change(c);

    /// Состояние входа в канбан, по которому строилась его страница.
    public string KanbanBuilt { get; private set; } = "";

    public string KanbanState()
    {
        var k = _s.Kanban;
        return $"{k.Api.SignedIn}|{k.Api.Email}|{k.WaitingForGoogle}|{k.SignInError}";
    }

    /// Построить страницу (заголовок добавляет хозяин).
    public void Build(string page)
    {
        switch (page)
        {
            case "island": BuildIsland(); break;
            case "dock": BuildDock(); break;
            case "start": BuildStart(); break;
            case "look": BuildLook(); break;
            case "widgets": BuildWidgets(); break;
            case "perf": BuildPerf(); break;
            case "kanban": BuildKanban(); break;
            case "keys": BuildKeys(); break;
            case "system": BuildSystem(); break;
            // Лист острова: виджеты по отдельности и разделы острова.
            case "w-sound": WidgetsAudio(withSwitch: true); break;
            case "w-media": WidgetsMedia(); break;
            case "w-shots": WidgetsShots(); break;
            case "w-bin": WidgetsBin(); break;
            case "w-clips": WidgetsClips(); break;
            case "w-charge": WidgetsCharge(); break;
            case "i-ribbon": IslandRibbon(withSwitch: true); break;
            case "i-move": IslandMove(); break;
        }
    }

    /// Остров (WSH-E23): включение, «Перенос» из дока (П24), порядок ленты, события.
    void BuildIsland()
    {
        if (!IslandSwitch()) return;
        Add(Section("Перенос из дока"));
        IslandMove();
        IslandRibbon(withSwitch: false);
    }

    /// Включение острова. false — выключен (дальше показывать нечего).
    bool IslandSwitch()
    {
        var island = _s.Config.Island;
        Add(Card(
            Switch("Остров у верхнего края", "Наведи курсор на самый верх экрана — откроется лента: звук, медиа, сеть, заряд, раскладка, снимки, корзина, полка. На каждом мониторе свой.",
                island.Enabled, on => { Change(c => c.Island.Enabled = on); _rebuild(); })));
        if (island.Enabled) return true;
        Add(Note("Пока остров выключен, всё остаётся в доке как раньше."));
        return false;
    }

    /// «Перенос» (П24): что есть в острове и в доке; убрать из дока может только Георгий.
    void IslandMove()
    {
        var data = _s.Config.Data.Island;
        Add(Note("Пока ты проверяешь новое, старое остаётся в доке. Убрать из дока можешь только ты — и в любой момент вернуть."));
        var moveRows = new List<UIElement>();
        foreach (var id in IslandSettings.Movable)
        {
            var inDock = !data.OutOfDock.Contains(id);
            var key = id;
            moveRows.Add(Row(IslandSettings.Title(id), inDock ? "В острове ✓ · в доке ✓" : "В острове ✓ · в доке —",
                inDock
                    ? Button("Проверил — убрать из дока", () => { Change(c => { if (!c.Island.OutOfDock.Contains(key)) c.Island.OutOfDock.Add(key); }); _rebuild(); })
                    : Button("Вернуть в док", () => { Change(c => c.Island.OutOfDock.Remove(key)); _rebuild(); })));
        }
        Add(Card(moveRows.ToArray()));
        Add(Note("Полка есть только в острове. Трей, часы, приложения и Пуск остаются в доке всегда."));
    }

    /// Лента и события острова.
    void IslandRibbon(bool withSwitch)
    {
        if (withSwitch && !IslandSwitch()) return;
        var island = _s.Config.Island;
        Add(Section("Лента"));
        var order = island.Order;
        var ribbonRows = new List<UIElement>();
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var title = IslandSettings.Title(id);
            var shown = island.Shown(id);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var up = Button("↑", () => { Change(c => c.Island.Ribbon = Island.Core.RibbonOrder.Move(_s.Config.Island.Order, id, -1)); _rebuild(); });
            up.IsEnabled = i > 0;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(up, $"{title}: левее");
            var down = Button("↓", () => { Change(c => c.Island.Ribbon = Island.Core.RibbonOrder.Move(_s.Config.Island.Order, id, 1)); _rebuild(); });
            down.IsEnabled = i < order.Count - 1;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(down, $"{title}: правее");
            right.Children.Add(up);
            right.Children.Add(down);
            var toggle = Toggle(shown, on => { Change(c => { c.Island.Hidden.Remove(id); if (!on) c.Island.Hidden.Add(id); }); _rebuild(); });
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"{title} в ленте");
            right.Children.Add(toggle);
            ribbonRows.Add(Row(title, shown ? "в ленте" : "скрыт из ленты", right));
        }
        Add(Card(ribbonRows.ToArray()));
        Add(Note("↑ и ↓ — порядок значков слева направо. Скрытый значок пропадает из ленты, но его события (например, низкий заряд) остаются."));

        Add(Section("События"));
        var lang = Island.Core.EventKind.Language;
        var charge = Island.Core.EventKind.Charge;
        var shot = Island.Core.EventKind.Shot;
        Add(Card(
            Switch("Смена раскладки", "Короткая пилюля с языком у верхнего края. В полноэкранных играх и видео не показывается.",
                island.EventOn(lang), on => Change(c => c.Island.Events[IslandSettings.EventKey(lang)] = on)),
            Switch("Низкий заряд", "Пилюля вместо уведомления Windows. В игре на весь экран — на другом мониторе, если он есть.",
                island.EventOn(charge), on => Change(c => c.Island.Events[IslandSettings.EventKey(charge)] = on)),
            Switch("Снимок готов", "Пилюля с миниатюрой: клик — галерея, миниатюру можно сразу перетащить в окно. В игре на весь экран — на другом мониторе или тихо в галерею.",
                island.EventOn(shot), on => Change(c => c.Island.Events[IslandSettings.EventKey(shot)] = on))));
        Add(Note("Значок записи с таймером виден всегда, пока идёт запись, — его выключить нельзя."));
    }

    void BuildDock()
    {
        var bars = _s.Config.Bars;
        Add(Card(
            Switch("Закрепить док", "Док всегда виден, окна программ не заходят под него. Выключено — док прячется и выезжает у нижнего края.",
                bars.DockPinned, on => Change(c => c.Bars.DockPinned = on))));

        Add(Section("Системный хвост"));
        var screens = _s.Monitors.All;
        var tail = (_s.Monitors.ByName(bars.Monitor) ?? _s.Monitors.Primary).Name;
        var combo = new ComboBox { MinWidth = 260 };
        for (var i = 0; i < screens.Count; i++)
        {
            var m = screens[i];
            combo.Items.Add($"Монитор {i + 1} — {m.Bounds.Right - m.Bounds.Left}×{m.Bounds.Bottom - m.Bounds.Top}, {m.Scale * 100:0} %{(m.Primary ? " (основной)" : "")}");
            if (m.Name == tail) combo.SelectedIndex = i;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < screens.Count)
            {
                var name = screens[combo.SelectedIndex].Name;
                Change(c => c.Bars.Monitor = name);
            }
        };
        var tailCfg = _s.Config.Tail;
        Add(Card(
            Row("Монитор для хвоста", "Полный хвост справа в доке. На остальных мониторах в доке только часы.", combo),
            Switch("Фоновые приложения", "Стрелка ⌃ с иконками программ из трея.", tailCfg.Tray, on => Change(c => c.Tail.Tray = on)),
            Switch("Сеть", "Значок сети: клик открывает список Wi-Fi.", tailCfg.Network, on => Change(c => c.Tail.Network = on)),
            Switch("Звук", "Значок громкости: колесо меняет громкость, клик открывает окошко звука.",
                _s.Config.WidgetEnabled("audio"), on => Change(_ => _s.Config.Widget("audio").Enabled = on)),
            Switch("Батарея", "Только на ноутбуке: на компьютере без батареи значка нет.", tailCfg.Battery, on => Change(c => c.Tail.Battery = on)),
            Switch("Язык ввода", "RU / EN, клик — выбор раскладки.", tailCfg.Language, on => Change(c => c.Tail.Language = on)),
            Switch("Уведомления", "Колокольчик: клик открывает центр уведомлений Windows.", tailCfg.Notifications, on => Change(c => c.Tail.Notifications = on)),
            Row("Часы", "Видны всегда.", null)));
    }

    void BuildStart()
    {
        var start = _s.Config.Start;
        Add(Card(
            Switch("Недавние файлы", "Блок с последними открытыми файлами под поиском.", start.RecentFiles, on => Change(c => c.Start.RecentFiles = on)),
            Switch("Отмечать новые программы", "Голубая точка и «Новое» у только что установленных программ во «Всех приложениях» и в поиске, сверху блок «Недавно установленные». Пропадает после первого запуска или через 7 дней.",
                start.MarkNew, on => Change(c => c.Start.MarkNew = on))));

        Add(Section("Быстрые папки"));
        var list = new StackPanel { Padding = new Thickness(0, 6, 0, 6) };
        foreach (var (id, title) in WindowsShell.Start.StartMenu.FolderChoices)
        {
            var folder = id;
            list.Children.Add(Check(title, start.Folders.Contains(id), on => Change(c =>
            {
                if (!on) c.Start.Folders.Remove(folder);
                else if (!c.Start.Folders.Contains(folder))
                    // Порядок всегда как в списке, а не как включали.
                    c.Start.Folders = WindowsShell.Start.StartMenu.FolderChoices.Select(x => x.Id).Where(x => x == folder || c.Start.Folders.Contains(x)).ToList();
            })));
        }
        Add(Card(list));
        Add(Note("Значки в нижней строке Пуска, рядом с кнопкой питания."));
    }

    void BuildLook()
    {
        var tint = _s.Config.Tint;
        Add(Card(
            Row("Фон дока и окошек", null, null),
            Choice(
            [
                ("Стекло (размытие того, что за ним)", !tint, () => Change(c => c.Appearance.Glass = "acrylic")),
                ("Тонировка (ровная тёмная заливка)", tint, () => Change(c => c.Appearance.Glass = "tint")),
            ])));
        Add(Note("Переключается сразу во всех окнах. Само ничего не меняет: только вручную."));
    }

    /// Временный значок виджета в хвосте: Когда нужно / Всегда / Никогда (design-v2 п. 2).
    Grid TempWidget(string id, string title, string hint, bool rebuild = false)
    {
        var modes = new[] { (ShellConfig.ShowWhenNeeded, "Когда нужно"), (ShellConfig.ShowAlways, "Всегда"), (ShellConfig.ShowNever, "Никогда") };
        var combo = new ComboBox { MinWidth = 160 };
        foreach (var (_, text) in modes) combo.Items.Add(text);
        combo.SelectedIndex = Array.FindIndex(modes, m => m.Item1 == _s.Config.TempWidgetMode(id));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var mode = modes[combo.SelectedIndex].Item1;
            Change(_ => _s.Config.SetTempWidgetMode(id, mode));
            if (rebuild) _rebuild();
        };
        return Row(title, hint, combo);
    }

    void BuildWidgets()
    {
        var audioOn = _s.Config.WidgetEnabled("audio");
        Add(Card(
            AudioSwitch(audioOn),
            MediaRow(),
            BinRow(),
            DesktopBinRow()));
        Add(Note("Временные значки стоят в хвосте левее ⌃. «Когда нужно» — появляются сами, «Всегда» — видны постоянно."));

        Add(Section("Снимки и запись экрана"));
        WidgetsShots();
        Add(Section("Буфер обмена"));
        WidgetsClips();
        Add(Section("Заряд устройств"));
        WidgetsCharge();
        if (audioOn) WidgetsAudio(withSwitch: false);
    }

    Grid AudioSwitch(bool audioOn) =>
        Switch("Звук", "Громкость, устройства, приложения и микрофон. Значок в доке справа.", audioOn, on =>
        {
            Change(_ => _s.Config.Widget("audio").Enabled = on);
            _rebuild();
        });

    Grid MediaRow() => TempWidget("media", "Сейчас играет", "Значок в хвосте дока, пока открыт плеер или вкладка с музыкой (играет или на паузе). Клик — обложка и кнопки, средний клик — пауза.");

    Grid BinRow() => TempWidget("bin", "Корзина в доке", "Значок в хвосте дока, пока в корзине что-то есть, и пока тащишь файлы из Проводника: брось на него — файлы уйдут в корзину. Клик — последние удалённые и «Восстановить».");

    Grid DesktopBinRow() => Switch("Убрать корзину с рабочего стола", "Пока работает оболочка. При выходе или если оболочка не поднимается, значок возвращается.", _s.Config.HideDesktopBin,
        on => Change(_ => _s.Config.SetHideDesktopBin(on)));

    void WidgetsMedia()
    {
        Add(Card(MediaRow()));
        Add(Note("В острове медиа всегда на своём месте в ленте: без музыки значок приглушён."));
    }

    void WidgetsBin()
    {
        Add(Card(BinRow(), DesktopBinRow()));
        Add(Section("Полка"));
        var shelf = _s.Shelf;
        var clear = Button("Очистить полку", () => { shelf.Clear(); _rebuild(); });
        clear.IsEnabled = shelf.Count > 0;
        Add(Card(Row("Полка файлов", shelf.Count == 0 ? "Пусто. Перетащи файл к верхнему краю экрана и брось на «Полку»." : $"Сейчас ссылок: {shelf.Count}. Очистка убирает только ссылки — файлы остаются на месте.", clear)));
        Add(Note("Перетащи файлы к верхнему краю экрана — остров покажет две цели: «Корзина» (удалить) и «Полка» (отложить ссылку)."));
    }

    void WidgetsShots()
    {
        var shotsOn = _s.Shots.Enabled;
        var shots = _s.Shots.Settings;
        var shotRows = new List<UIElement>
        {
            Switch("Снимки и запись экрана", "PrtSc — снимок (область, окно или весь монитор), Shift+PrtSc — запись, ещё раз Shift+PrtSc — стоп. Выключено — клавиши работают как в Windows.", shotsOn, on =>
            {
                Change(_ => _s.Config.Widget("shots").Enabled = on);
                _rebuild();
            }),
        };
        if (shotsOn)
        {
            shotRows.Add(Switch("Значок «Снимки» в хвосте", "Клик — последние снимки и записи, наведение — крупное превью последнего. Во время записи на его месте красная точка с таймером.",
                shots.Icon, on => Change(_ => _s.Shots.Settings.Icon = on)));
            shotRows.Add(Switch("Win+Shift+S — тоже наш снимок", "Выключено — Win+Shift+S открывает «Ножницы» Windows.",
                shots.WinShiftS, on => Change(_ => _s.Shots.Settings.WinShiftS = on)));
            shotRows.Add(Switch("Звук системы в записи", "То, что слышно в колонках или наушниках. Можно включить и выключить прямо во время записи.",
                shots.SystemSound, on => Change(_ => _s.Shots.Settings.SystemSound = on)));
            shotRows.Add(Switch("Микрофон в записи", "Можно включить и выключить прямо во время записи.",
                shots.Mic, on => Change(_ => _s.Shots.Settings.Mic = on)));
            shotRows.Add(Row("Папки", "Снимки — «Изображения\\Windows Shell», записи — «Видео\\Записи экрана».", new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    Button("Снимки", WindowsShell.Shots.ShotsService.OpenFolder),
                    Button("Записи", () => WindowsShell.Apps.Launcher.Run("explorer.exe", args: $"\"{WindowsShell.Shots.ShotFiles.VideosDir}\"")),
                },
            }));
        }
        Add(Card(shotRows.ToArray()));
        Add(Note("Снимки и записи старше 3 дней удаляются сами, насовсем. Нужное сохрани: «Сохранить как…» или просто перетащи в папку."));
    }

    void WidgetsClips()
    {
        var clipsOn = _s.Clips.Enabled;
        var clipRows = new List<UIElement>
        {
            Switch("История буфера обмена (Win+V)", "Win+V — окошко у текстового курсора: закреплённое и всё скопированное за 3 дня, печатаешь — поиск, Enter — вставить. Выключено — Win+V открывает историю Windows, а оболочка ничего не запоминает.", clipsOn, on =>
            {
                Change(_ => _s.Config.Widget("clipboard").Enabled = on);
                _rebuild();
            }),
        };
        if (clipsOn)
        {
            var store = _s.Clips.Store;
            Button? clear = null;
            var confirm = false;
            clear = Button("Очистить", () =>
            {
                // Первое нажатие спрашивает, второе очищает.
                if (!confirm) { confirm = true; clear!.Content = Text("Точно очистить?", 13); return; }
                _s.Clips.ClearHistory();
                _rebuild();
            });
            clear.IsEnabled = store.History.Count > 0;
            clipRows.Add(Row("История", $"Сейчас {store.History.Count}, закреплённых {store.Pinned.Count}. Очистка не трогает закреплённое.", clear));
        }
        Add(Card(clipRows.ToArray()));
        Add(Note("История хранится только на этом компьютере, 3 дня; закреплённое — всегда. Пароли из менеджеров паролей не запоминаются (они сами помечают такие данные)."));

    }

    void WidgetsCharge()
    {
        var charge = _s.Charge;
        var chargeRows = new List<UIElement>
        {
            TempWidget("charge", "Значок «Заряд»", "Сам находит Bluetooth-клавиатуры, мыши, геймпады, наушники, гарнитуры и мыши Logitech (без G HUB). «Когда нужно» — значок появляется, когда что-то садится. «Никогда» — ни значка, ни уведомлений.", rebuild: true),
        };
        if (charge.Enabled)
        {
            var thresholds = ChargeSettings.Thresholds;
            var combo = new ComboBox { MinWidth = 120 };
            foreach (var t in thresholds) combo.Items.Add($"{t} %");
            combo.SelectedIndex = Math.Max(0, Array.IndexOf(thresholds, charge.Threshold));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                var value = thresholds[combo.SelectedIndex];
                Change(_ => _s.Charge.Settings.Threshold = value);
            };
            chargeRows.Add(Row("Порог «пора зарядить»", $"Уведомление при этом заряде и ещё одно при {WindowsShell.Charge.ChargeService.Second} %. Снова — только после зарядки.", combo));
            chargeRows.Add(Switch("Сигнал в игре", "В полноэкранной игре Windows прячет уведомления: тогда мягкий сигнал в наушники и плашка в правом верхнем углу на 5 секунд.",
                charge.Settings.GameSound, on => Change(_ => _s.Charge.Settings.GameSound = on)));
            chargeRows.Add(Row("Как это выглядит", "Сигнал и плашка, как в игре.", Button("Показать", () => _s.Charge.Preview())));
            foreach (var (key, name) in charge.Settings.Ignored)
            {
                var k = key;
                chargeRows.Add(Row(name, "Не следим (скрыто правым кликом в окошке).", Button("Вернуть", () => { _s.Charge.Unignore(k); _rebuild(); })));
            }
        }
        Add(Card(chargeRows.ToArray()));
        if (charge.Enabled)
            Add(Note(charge.Watched.Any() ? "Сейчас: " + string.Join(", ", charge.Watched.Select(d => d.Percent is { } p ? $"{d.Name} {p} %" : $"{d.Name} (не подключено)")) + "." : "Пока не найдено ни одного устройства с батареей."));
    }

    void WidgetsAudio(bool withSwitch)
    {
        if (withSwitch)
        {
            var on = _s.Config.WidgetEnabled("audio");
            Add(Card(AudioSwitch(on)));
            if (!on) return;
        }
        var a = _s.AudioSettings;
        Add(Section("Звук"));
        var value = Text($"{a.Step} %", 13, secondary: true);
        var slider = new Slider { Minimum = 1, Maximum = 10, StepFrequency = 1, Value = a.Step, IsThumbToolTipEnabled = false, Width = 180 };
        slider.ValueChanged += (_, e) =>
        {
            value.Text = $"{(int)e.NewValue} %";
            Change(_ => _s.AudioSettings.Step = (int)e.NewValue);
        };
        var step = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { slider, value } };
        Add(Card(
            Row("Шаг колеса мыши", "На сколько меняется громкость за один щелчок колеса над значком.", step),
            Switch("Громкость приложений", "Свёрнутый список в окошке звука.", a.ShowApps, on => Change(_ => _s.AudioSettings.ShowApps = on)),
            Switch("Микрофон", "Ползунок микрофона в окошке звука.", a.ShowMic, on => Change(_ => _s.AudioSettings.ShowMic = on))));

        // Профили: устройство и громкость одним кликом или горячей клавишей.
        Add(Section("Профили звука"));
        var rows = new List<UIElement>();
        foreach (var p in a.Profiles)
        {
            var profile = p;
            var hotkey = new HotkeyBox(p.Hotkey, k => Change(_ =>
            {
                var list = _s.AudioSettings.Profiles;
                var i = list.FindIndex(x => x.Id == profile.Id);
                if (i >= 0) { list[i] = list[i] with { Hotkey = k }; _s.AudioSettings.Profiles = list; }
            }));
            hotkey.Width = 180;
            var del = new Button
            {
                Content = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 14 },
                Width = 36,
                Height = 32,
                Padding = new Thickness(0),
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            ToolTipService.SetToolTip(del, "Удалить профиль");
            del.Click += (_, _) =>
            {
                Change(_ => _s.AudioSettings.Profiles = _s.AudioSettings.Profiles.Where(x => x.Id != profile.Id).ToList());
                _rebuild();
            };
            rows.Add(Row(p.Name, $"Громкость {p.Volume} %", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { hotkey, del } }));
        }
        var devices = _s.Audio.Devices();
        var current = devices.FirstOrDefault(d => d.Id == _s.Audio.DefaultId());
        if (current != null)
        {
            var name = $"{AudioSettings.ShortName(current.Name, devices.Select(d => d.Name))} {_s.Audio.Volume}%";
            rows.Add(Row("Добавить профиль", "Запоминает текущее устройство и громкость.", Button($"＋ «{name}»", () =>
            {
                Change(_ =>
                {
                    var list = _s.AudioSettings.Profiles;
                    var n = list.Count + 1;
                    while (list.Any(x => x.Id == $"profile-{n}")) n++;
                    list.Add(new AudioProfile($"profile-{n}", name, current.Id, _s.Audio.Volume, null));
                    _s.AudioSettings.Profiles = list;
                });
                _rebuild();
            })));
        }
        var next = new HotkeyBox(a.NextOutputHotkey, k => Change(_ => _s.AudioSettings.NextOutputHotkey = k)) { Width = 224 };
        rows.Add(Row("Следующее устройство вывода", "Горячая клавиша переключает звук на следующее устройство.", next));
        Add(Card(rows.ToArray()));
        Add(Note("Клик по полю клавиши, потом нужное сочетание. Backspace убирает сочетание, Esc отменяет."));
    }

    void BuildPerf()
    {
        var perf = _s.Perf;
        var p = perf.Settings;
        var overlayRows = new List<UIElement>
        {
            Switch("Оверлей в игре", "Цифры в углу экрана, только когда в фокусе игра. Клики проходят сквозь него, фокус он не берёт.", p.Overlay, on =>
            {
                Change(_ => _s.Perf.Settings.Overlay = on);
                _rebuild();
            }),
        };
        if (p.Overlay)
        {
            overlayRows.Add(Row("Что показывать", "Галочки — что видно, стрелки — порядок. Соседние пары (FPS и время кадра, процессор % и °C) стоят вместе.", null));
            var order = p.Order;
            var shown = p.Shown;
            for (var i = 0; i < order.Count; i++)
            {
                var id = order[i];
                var index = i;
                var line = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, -6, 0, 0) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(Check(PerfSettings.MetricName(id), shown.Contains(id), on => Change(_ =>
                {
                    var set = _s.Perf.Settings.Shown;
                    if (on) set.Add(id); else set.Remove(id);
                    _s.Perf.Settings.Shown = set;
                })));
                var up = Arrow("", "Выше", index > 0, () => Move(index, -1));
                var down = Arrow("", "Ниже", index < order.Count - 1, () => Move(index, 1));
                Grid.SetColumn(up, 1);
                Grid.SetColumn(down, 2);
                line.Children.Add(up);
                line.Children.Add(down);
                overlayRows.Add(line);
            }
            overlayRows.Add(Row("Расположение", null, Combo(["В строку", "Столбиком"], p.Column ? 1 : 0, i => _s.Perf.Settings.Column = i == 1)));
            string[] corners = [PerfSettings.TopLeft, PerfSettings.TopRight, PerfSettings.BottomLeft, PerfSettings.BottomRight];
            overlayRows.Add(Row("Угол экрана", null, Combo(["Левый верхний", "Правый верхний", "Левый нижний", "Правый нижний"],
                Array.IndexOf(corners, p.Corner), i => _s.Perf.Settings.Corner = corners[i])));
            var sizes = PerfSettings.FontSizes;
            overlayRows.Add(Row("Размер текста", null, Combo(["Мелкий", "Средний", "Крупный"],
                Math.Max(0, Array.IndexOf(sizes, p.FontSize)), i => _s.Perf.Settings.FontSize = sizes[i])));
            var bgValue = Text($"{p.Background} %", 13, secondary: true);
            var bg = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 5, Value = p.Background, IsThumbToolTipEnabled = false, Width = 180 };
            bg.ValueChanged += (_, e) =>
            {
                bgValue.Text = $"{(int)e.NewValue} %";
                Change(_ => _s.Perf.Settings.Background = (int)e.NewValue);
            };
            overlayRows.Add(Row("Фон", "0 % — только цифры с тенью.", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { bg, bgValue } }));
            int[] warns = [0, 30, 45, 60, 90, 120];
            overlayRows.Add(Row("FPS ниже — жёлтым", "Температура от 80 °C жёлтая, от 90 °C красная.",
                Combo(warns.Select(w => w == 0 ? "Не подсвечивать" : $"{w}").ToArray(), Math.Max(0, Array.IndexOf(warns, p.FpsWarn)), i => _s.Perf.Settings.FpsWarn = warns[i])));
            overlayRows.Add(Row("Включить и выключить в игре", "Сочетание работает, только пока в фокусе игра.",
                new HotkeyBox(p.Hotkey, k => Change(_ => _s.Perf.Settings.Hotkey = k)) { Width = 200 }));
            overlayRows.Add(Row("Как это выглядит", "Оверлей на 5 секунд на этом мониторе, FPS для примера.", Button("Показать", () => _s.Perf.Preview())));
        }
        Add(Card(overlayRows.ToArray()));

        Add(Section("Окно «Монитор ПК»"));
        var windowRows = new List<UIElement>
        {
            Switch("Окно «Монитор ПК»", "Всё сразу с графиками за минуту: FPS (когда идёт игра), процессор, видеокарта, память, видеопамять, сеть. Тянется за любое место, помнит, где стоит.", p.Window,
                on => Change(_ => _s.Perf.Settings.Window = on)),
        };
        if (p.Window)
            windowRows.Add(Switch("Поверх всех окон", "Выключено — обычное окно.", p.WindowTopmost, on => Change(_ => _s.Perf.Settings.WindowTopmost = on)));
        Add(Card(windowRows.ToArray()));
        Add(Note("Правый клик по окну — «Поверх всех окон», настройки, закрыть."));

        Add(Section("FPS"));
        var state = PerfAccess.Current;
        UIElement? grant = null;
        if (state == PerfAccess.State.Needed)
            grant = Button("Разрешить", async () =>
            {
                if (await PerfAccess.Grant()) _rebuild();
            });
        Add(Card(Row(state switch
        {
            PerfAccess.State.Ready => "Разрешение есть",
            PerfAccess.State.AfterSignIn => "Разрешение дано",
            _ => "Нужно разрешение администратора",
        }, state switch
        {
            PerfAccess.State.Ready => "Кадры считаются по событиям Windows, как в Intel PresentMon. В игру ничего не встраивается.",
            PerfAccess.State.AfterSignIn => "Заработает после выхода из Windows и входа снова (или перезагрузки).",
            _ => $"Один раз: оболочка добавит тебя в группу «{PerfAccess.GroupName}», чтобы читать события показа кадров. В игру ничего не встраивается.",
        }, grant)));
        Add(Note("Считаются кадры, реально показанные на экране, среднее за полсекунды. Генерация кадров (DLSS FG, AFMF) входит в число, поэтому оно может отличаться от счётчика в игре."));

        Add(Section("Что считается игрой"));
        var gameRows = new List<UIElement>();
        foreach (var (exe, game) in p.Marks.OrderBy(m => Path.GetFileName(m.Key)))
        {
            var e = exe;
            gameRows.Add(Row(Path.GetFileName(exe), (game ? "Игра (отмечено вручную) · " : "Не игра (отмечено вручную) · ") + Path.GetDirectoryName(exe),
                Button("Убрать", () => { _s.Perf.Mark(e, null); _rebuild(); })));
        }
        var pick = new Button { Content = Text("Выбрать программу…", 13), MinWidth = 120 };
        pick.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            foreach (var (exe, name) in _s.Perf.RunningApps())
            {
                var e = exe;
                var sub = new MenuFlyoutSubItem { Text = $"{name} — {(_s.Perf.Why(exe) is { } why ? "игра (" + why + ")" : "не игра")}" };
                var yes = new MenuFlyoutItem { Text = "Это игра" };
                yes.Click += (_, _) => { _s.Perf.Mark(e, true); _rebuild(); };
                var no = new MenuFlyoutItem { Text = "Не игра" };
                no.Click += (_, _) => { _s.Perf.Mark(e, false); _rebuild(); };
                sub.Items.Add(yes);
                sub.Items.Add(no);
                menu.Items.Add(sub);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = "Нет открытых программ", IsEnabled = false });
            menu.ShowAt(pick);
        };
        gameRows.Add(Row("Отметить вручную", "Из открытых сейчас программ: «это игра» или «не игра».", pick));
        Add(Card(gameRows.ToArray()));
        Add(Note($"Сама: игры, которые знает Windows (игровая панель), и всё из папок Steam, Epic, GOG, Ubisoft, Xbox, EA и Riot. Браузеры и плееры не считаются никогда. Сейчас: папок игр {Games.Roots.Count}, игр в списке Windows {Games.KnownCount}."));
        if (perf.CpuTempSource != "нет" || perf.GpuName.Length > 0)
            Add(Note($"Видеокарта: {(perf.GpuName.Length > 0 ? perf.GpuName : "—")}. Температура процессора: {perf.CpuTempSource}."));

        void Move(int index, int delta)
        {
            Change(_ =>
            {
                var list = _s.Perf.Settings.Order;
                (list[index], list[index + delta]) = (list[index + delta], list[index]);
                _s.Perf.Settings.Order = list;
            });
            _rebuild();
        }

        ComboBox Combo(string[] items, int selected, Action<int> pick)
        {
            var combo = new ComboBox { MinWidth = 180 };
            foreach (var item in items) combo.Items.Add(item);
            combo.SelectedIndex = Math.Max(0, selected);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                var i = combo.SelectedIndex;
                Change(_ => pick(i));
            };
            return combo;
        }

        static Button Arrow(string glyph, string tip, bool enabled, Action click)
        {
            var b = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontFamily = Theme.Icons, FontSize = 12 },
                Width = 32, Height = 30, Padding = new Thickness(0),
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                IsEnabled = enabled,
            };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
    }

    void BuildKanban()
    {
        var k = _s.Kanban;
        var s = k.Settings;
        KanbanBuilt = KanbanState();
        Add(Card(Switch("Канбан", "Твой канбан с сайта, не копия: что меняешь здесь, сразу видно там, и наоборот. Значок в хвосте дока — быстрая задача, на нём число «Нужно от тебя».",
            k.Enabled, on =>
            {
                Change(_ => _s.Config.Widget("kanban").Enabled = on);
                _rebuild();
            })));
        if (!k.Enabled) return;

        Add(Section("Вход"));
        if (k.Api.SignedIn)
            Add(Card(Row($"Вошёл: {k.Api.Email}", "Вход хранится зашифрованным, только для твоей учётной записи Windows.", Button("Выйти", k.SignOut))));
        else
            Add(Card(new Border { Padding = new Thickness(0, 14, 0, 14), MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Child = WindowsShell.Kanban.SignInView.Build(k) }));

        ComboBox Combo(string[] items, int selected, Action<int> pick)
        {
            var combo = new ComboBox { MinWidth = 200 };
            foreach (var item in items) combo.Items.Add(item);
            combo.SelectedIndex = Math.Max(0, selected);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0) return;
                var i = combo.SelectedIndex;
                Change(_ => pick(i));
            };
            return combo;
        }

        Add(Section("Окно канбана"));
        var rows = new List<UIElement>
        {
            Switch("Окно канбана", "Доска как на сайте: колонки Hold · Backlog · В работе · Нужно от тебя · Готово, задачи по эпикам, поиск, архив. Клик — задача с описанием, шагами и комментариями. Помнит монитор, место и размер.",
                s.Window, on => Change(_ => _s.Kanban.Settings.Window = on)),
            Switch("Поверх всех окон", "Выключено — обычное окно.", s.WindowTopmost, on => Change(_ => _s.Kanban.Settings.WindowTopmost = on)),
        };
        var shown = s.Shown;
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var c in KanbanSettings.Columns)
        {
            var id = c;
            columns.Children.Add(Check(WindowsShell.Kanban.KStatus.Name(id), shown.Contains(id), on => Change(_ =>
            {
                var set = _s.Kanban.Settings.Shown;
                if (on && !set.Contains(id)) set.Add(id);
                if (!on) set.Remove(id);
                _s.Kanban.Settings.Shown = set;
            })));
        }
        rows.Add(Row("Колонки", null, columns));
        rows.Add(Switch("Колонка «Готово»", "Последние пять закрытых, остальные — в архиве (кнопка «Архив» в окне).", s.ShowDone,
            on => Change(_ => _s.Kanban.Settings.ShowDone = on)));
        rows.Add(Row("Карточки", null, Combo(["Обычные", "Компактные"], s.Compact ? 1 : 0, i => _s.Kanban.Settings.Compact = i == 1)));
        Add(Card(rows.ToArray()));

        Add(Section("Значок"));
        Add(Card(Switch("Число «Нужно от тебя»", "Кружок на значке канбана в доке и в ленте острова.", s.Badge, on => Change(_ => _s.Kanban.Settings.Badge = on))));
        Add(Note("Задачи читаются раз в 15 секунд, пока окно открыто, и раз в 2 минуты для значка. В окне: Ctrl+N — новая задача, F5 — обновить, Esc — закрыть карточку. Правый клик по карточке — перенести в другую колонку."));
    }

    void BuildKeys()
    {
        var k = _s.Config.Keys;
        Add(Card(
            Switch("Win — наш Пуск", "Выключено — клавиша Win открывает Пуск Windows. Ctrl+Esc открывает Пуск Windows всегда.",
                k.WinStart, on => Change(c => c.Keys.WinStart = on)),
            Switch("Win+X — служебное меню", "Диспетчер задач, терминал, питание и прочее с буквами-ускорителями.",
                k.WinX, on => Change(c => c.Keys.WinX = on)),
            Switch("Win+S и Win+Q — поиск", "Открывают наш Пуск сразу с поиском.",
                k.WinSearch, on => Change(c => c.Keys.WinSearch = on)),
            Switch("Win+1…9, Win+T, Win+B — док", "Цифра открывает программу из дока по порядку, Win+T и Win+B — ходить по доку и хвосту стрелками.",
                k.DockKeys, on => Change(c => c.Keys.DockKeys = on))));
        Add(Note("Выключенное сочетание работает как в Windows. Остальные (Win+E, Win+D, Win+L…) всегда как в Windows. Горячие клавиши звука, снимков (PrtSc) и буфера обмена (Win+V) — на странице «Виджеты»."));
    }

    void BuildSystem()
    {
        Add(Card(
            Switch("Прятать панель задач Windows", "Её работу делают док и хвост. Вернуть на время: Ctrl+Alt+Shift+T или меню значка оболочки.",
                _s.Config.Tail.HideTaskbar, on => Change(c => c.Tail.HideTaskbar = on))));
        Add(Note("Если оболочка упадёт или зависнет, она запустится снова сама. Если падает раз за разом, вернётся панель задач Windows."));

        Add(Card(
            Switch("Запускать вместе с Windows", "Оболочка появляется сама после входа в систему.", Autostart.IsOn, on =>
            {
                Autostart.Set(on);
                _rebuild();
            })));
        Add(Note("Оболочка запускается сразу при входе, вместе с Проводником, раньше остальных программ из автозагрузки."));

        Add(Section("Оболочка"));
        Add(Card(
            Row("Перезапустить оболочку", "Если что-то выглядит не так. Ctrl+Alt+Shift+R или ярлык «Windows Shell» работают, даже когда оболочка зависла или закрыта.", Button("Перезапустить", () => _s.Restart())),
            Row("Папка оболочки", "Настройки (config.json), журнал (log.txt), отчёты о сбоях (crashes) и ярлыки, брошенные в категории.", Button("Открыть", () =>
                WindowsShell.Apps.Launcher.Run("explorer.exe", args: $"\"{ShellConfig.Dir}\"")))));
    }
}
