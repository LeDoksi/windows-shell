using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsShell.Config;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using static WindowsShell.Settings.Rows;

namespace WindowsShell.Settings;

/// Окно настроек (WSH-39, раздел E8 списка проверки): док и хвост, верхняя панель, Пуск, стекло,
/// виджеты, клавиши, автозапуск. Обычное окно со своей строкой заголовка и в Alt+Tab, одно на
/// всю оболочку. Всё применяется сразу, без кнопки «Сохранить» и без перезапуска.
public sealed class SettingsWindow
{
    static SettingsWindow? _open;
    static string _lastPage = "dock";

    /// Открыть окно на странице `page` (null — на последней открытой); уже открыто — вывести вперёд.
    public static void Open(Services services, string? page = null)
    {
        if (_open is { } w)
        {
            if (page != null) w.Go(page);
            w.Activate();
            return;
        }
        _open = new SettingsWindow(services, page ?? _lastPage);
    }

    static readonly (string Id, string Title, string Glyph)[] Pages =
    [
        ("dock", "Док и хвост", ""),
        ("panel", "Верхняя панель", ""),
        ("start", "Пуск", ""),
        ("look", "Внешний вид", ""),
        ("widgets", "Виджеты", ""),
        ("keys", "Клавиши", ""),
        ("system", "Система", ""),
    ];

    readonly Services _s;
    readonly Window _window;
    readonly IntPtr _hwnd;
    readonly Grid _root;
    readonly NavigationView _nav;
    readonly StackPanel _body = new() { MaxWidth = 760, Padding = new Thickness(36, 24, 36, 36), HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ScrollViewer _scroll;
    string _page;
    // Изменение пришло из этого окна: страницу не перестраиваем (фокус и прокрутка остаются).
    bool _self;

    SettingsWindow(Services services, string page)
    {
        _s = services;
        _page = page;
        _window = new Window { Title = "Настройки — Windows Shell" };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsPaneToggleButtonVisible = false,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsTitleBarAutoPaddingEnabled = false,
            OpenPaneLength = 240,
            Content = _scroll,
        };
        foreach (var (id, title, glyph) in Pages)
            _nav.MenuItems.Add(new NavigationViewItem { Content = title, Tag = id, Icon = new FontIcon { Glyph = glyph, FontFamily = Theme.Icons } });
        _nav.SelectionChanged += (_, e) =>
        {
            if (e.SelectedItem is NavigationViewItem { Tag: string id } && id != _page) { _page = id; Build(); }
        };
        _root = new Grid { RequestedTheme = ElementTheme.Dark, Children = { _nav } };
        _window.Content = _root;
        ApplySurface();
        Glass.Changed += ApplySurface;

        var presenter = OverlappedPresenter.Create();
        // Панели задач Windows скоро не будет: свёрнутое окно было бы негде найти.
        presenter.IsMinimizable = false;
        _window.AppWindow.SetPresenter(presenter);
        SetDark(_hwnd);
        try { _window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "shell.ico")); } catch { }
        Place();

        _s.Config.Changed += OnConfigChanged;
        _s.Monitors.Changed += OnMonitorsChanged;
        _window.Closed += (_, _) =>
        {
            _s.Config.Changed -= OnConfigChanged;
            _s.Monitors.Changed -= OnMonitorsChanged;
            Glass.Changed -= ApplySurface;
            _lastPage = _page;
            _open = null;
        };

        Go(page);
        Activate();
        Log.Write("окно настроек открыто");
    }

    /// По центру монитора с курсором, 900×680 на глаз при любом масштабе.
    void Place()
    {
        GetCursorPos(out var p);
        var screen = _s.Monitors.At(p.X, p.Y);
        var work = screen.Work;
        var w = Math.Min((int)(900 * screen.Scale), work.Right - work.Left - 40);
        var h = Math.Min((int)(680 * screen.Scale), work.Bottom - work.Top - 40);
        var x = work.Left + (work.Right - work.Left - w) / 2;
        var y = work.Top + (work.Bottom - work.Top - h) / 2;
        _window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    void Activate()
    {
        _window.Activate();
        ShellWindow.ForceForeground(_hwnd);
    }

    /// Mica, а при «Тонировке» — ровная тёмная заливка (правило 0.12).
    void ApplySurface()
    {
        if (Glass.Tint)
        {
            _window.SystemBackdrop = null;
            _root.Background = Theme.Brush(Theme.TintDock);
        }
        else
        {
            if (_window.SystemBackdrop is not MicaBackdrop) _window.SystemBackdrop = new MicaBackdrop();
            _root.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
        }
    }

    void Go(string page)
    {
        if (Pages.All(x => x.Id != page)) page = "dock";
        _page = page;
        _nav.SelectedItem = _nav.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == page);
        Build();
    }

    void OnConfigChanged()
    {
        // Поменяли в меню трея или в другом окошке — страница показывает свежее.
        if (!_self) Build();
    }

    void OnMonitorsChanged()
    {
        if (_page == "dock") Build();
    }

    /// Изменить конфиг из этого окна.
    void Change(Action<ConfigData> change)
    {
        _self = true;
        try { _s.Config.Update(change); }
        finally { _self = false; }
    }

    void Build()
    {
        var offset = _scroll.VerticalOffset;
        var samePage = _body.Tag as string == _page;
        _body.Children.Clear();
        _body.Tag = _page;
        _body.Children.Add(Title(Pages.First(x => x.Id == _page).Title));
        switch (_page)
        {
            case "dock": BuildDock(); break;
            case "panel": BuildPanel(); break;
            case "start": BuildStart(); break;
            case "look": BuildLook(); break;
            case "widgets": BuildWidgets(); break;
            case "keys": BuildKeys(); break;
            case "system": BuildSystem(); break;
        }
        if (samePage) _scroll.DispatcherQueue.TryEnqueue(() => _scroll.ChangeView(null, offset, null, disableAnimation: true));
        else _scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    void Add(UIElement e) => _body.Children.Add(e);

    // ---------- страницы ----------

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

    void BuildPanel()
    {
        Add(Card(
            Switch("Закрепить панель", "Панель всегда видна сверху, окна программ не заходят под неё. Выключено — выезжает у верхнего края любого монитора.",
                _s.Config.Bars.LauncherPinned, on => Change(c => c.Bars.LauncherPinned = on))));
        Add(Note("Категории добавляются кнопкой ＋ на самой панели, правый клик по категории — переименовать, цвет, удалить."));
    }

    void BuildStart()
    {
        var start = _s.Config.Start;
        Add(Card(
            Switch("Недавние файлы", "Блок с последними открытыми файлами под поиском.", start.RecentFiles, on => Change(c => c.Start.RecentFiles = on))));

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
            Row("Фон дока, панели и окошек", null, null),
            Choice(
            [
                ("Стекло (размытие того, что за ним)", !tint, () => Change(c => c.Appearance.Glass = "acrylic")),
                ("Тонировка (ровная тёмная заливка)", tint, () => Change(c => c.Appearance.Glass = "tint")),
            ])));
        Add(Note("Переключается сразу во всех окнах. Само ничего не меняет: только вручную."));
    }

    void BuildWidgets()
    {
        var audioOn = _s.Config.WidgetEnabled("audio");
        Add(Card(
            Switch("Звук", "Громкость, устройства, приложения и микрофон. Значок в доке справа.", audioOn, on =>
            {
                Change(_ => _s.Config.Widget("audio").Enabled = on);
                Build();
            }),
            Switch("Сейчас играет", "Музыка и видео в верхней панели, пока играют.", _s.Config.WidgetEnabled("media"), on =>
                Change(_ => _s.Config.Widget("media").Enabled = on))));
        if (!audioOn) return;

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
                Build();
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
                Build();
            })));
        }
        var next = new HotkeyBox(a.NextOutputHotkey, k => Change(_ => _s.AudioSettings.NextOutputHotkey = k)) { Width = 224 };
        rows.Add(Row("Следующее устройство вывода", "Горячая клавиша переключает звук на следующее устройство.", next));
        Add(Card(rows.ToArray()));
        Add(Note("Клик по полю клавиши, потом нужное сочетание. Backspace убирает сочетание, Esc отменяет."));
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
        Add(Note("Выключенное сочетание работает как в Windows. Остальные (Win+E, Win+D, Win+L…) всегда как в Windows. Горячие клавиши звука — на странице «Виджеты»."));
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
                Build();
            })));
        Add(Note("Оболочка запускается сразу при входе, вместе с Проводником, раньше остальных программ из автозагрузки."));

        Add(Section("Оболочка"));
        Add(Card(
            Row("Перезапустить оболочку", "Если что-то выглядит не так. Ctrl+Alt+Shift+R или ярлык «Windows Shell» работают, даже когда оболочка зависла или закрыта.", Button("Перезапустить", () => _s.Restart())),
            Row("Папка оболочки", "Настройки (config.json), журнал (log.txt), отчёты о сбоях (crashes) и ярлыки, брошенные в категории.", Button("Открыть", () =>
                WindowsShell.Apps.Launcher.Run("explorer.exe", args: $"\"{ShellConfig.Dir}\"")))));
    }
}
