using System.Diagnostics;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Audio;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell;

/// Оболочка целиком: док на каждом мониторе (раздел 9), значок в трее с меню (раздел 5),
/// горячие клавиши звука, стекло. Следит за мониторами и конфигом. Верхней панели в v2 нет:
/// категории живут в Пуске (design-v2 п. 1).
public sealed class ShellHost
{
    readonly Services _s;
    readonly Dictionary<string, Dock.Dock> _docks = [];
    readonly Hotkeys _hotkeys;
    TrayIcon? _tray;
    PathWatch? _pathWatch;
    string _hotkeysKey = "";
    bool _restarting, _leavingForRestart;
    /// Панель задач вернули вручную (меню трея, Ctrl+Alt+Shift+T): не прячем до перезапуска.
    bool _taskbarReturned;
    readonly HashSet<IntPtr> _announced = [];

    public ShellHost(Services services)
    {
        _s = services;
        _hotkeys = new Hotkeys(services.Messages);
        _s.ToggleStart = dock => ToggleStart(dock);
        _s.ToggleTools = ToggleTools;
        _s.OpenSettings = page => Settings.SettingsWindow.Open(_s, page);
        _s.Restart = Restart;
        _s.RestoreTaskbar = () =>
        {
            _taskbarReturned = true;
            Taskbar.Restore("вручную");
            foreach (var d in _docks.Values) d.Move(d.Screen, d.FullTail, force: true);
        };
    }

    public void Start()
    {
        Glass.Set(_s.Config.Tint);
        // Свой трей и скрытие панели задач (WSH-E9) — до доков: док сразу встаёт к нижнему краю.
        _s.Tray.Start();
        ApplyTaskbar();
        ApplyDesktopBin();
        _s.Tray.Balloon += () => { _s.TrayAttention = true; _s.Notifications.NoteBalloon(); foreach (var d in _docks.Values) d.UpdateTail(); };
        _s.Messages.TaskbarCreated += Taskbar.OnTaskbarCreated;
        _s.Tracker.Changed += AnnounceButtons;
        AnnounceButtons();
        Sync();
        _tray = new TrayIcon(_s.Messages, _s.Menu, TrayMenu);
        _s.TrayFlyout.ShellMenu = TrayMenu;
        _pathWatch = new PathWatch(_s.Messages, _s.Config);
        _s.Config.Changed += OnConfigChanged;
        _s.Monitors.Changed += Sync;
        _s.Monitors.WorkAreaChanged += Sync;
        _s.Messages.Resumed += () =>
        {
            // После сна часы и мониторы сразу правильные (раздел 7).
            foreach (var d in _docks.Values) d.UpdateClock();
            _s.Monitors.Refresh();
            _s.Tracker.Scan();
        };
        _s.Messages.Flash += _s.Tracker.Flash;
        _s.Messages.Message += (msg, w, _) =>
        {
            // Windows завершает работу: убираем резерв места и значок, сторож не поднимает.
            if (msg == 0x0016 /* WM_ENDSESSION */ && w != IntPtr.Zero)
            {
                Watchdog.Leaving(Watchdog.Leave.Session);
                Shutdown();
            }
            // Просьбы сторожа: ярлык «Windows Shell» или Ctrl+Alt+Shift+R, остановка перед обновлением.
            else if (msg == Watchdog.ShellRestart) Restart();
            else if (msg == Watchdog.ShellQuit) Quit();
            else if (msg == Watchdog.ShellSimulate) Watchdog.Simulate((int)w);
        };
        ApplyWidgets();
        ApplyKeys();
        RegisterHotkeys();
        _s.Keys.Start += () => ToggleStart(null);
        _s.Keys.Snapshot += record => { if (record) _s.Shots.Record(); else _s.Shots.Snap(); };
        // Миниатюра снимка — над значком «Снимки» в доке монитора хвоста.
        _s.Shots.Anchor = () => _docks.Values.FirstOrDefault(d => d.FullTail)?.ShotsAnchorPx();
        // Предупреждения о заряде в игре: сигнал и плашка на мониторе с полноэкранной игрой.
        _s.Charge.GameScreen = () =>
            _s.Perf.GameScreen is { } game ? _s.Monitors.All.FirstOrDefault(m => m.Bounds.Left == game.Left && m.Bounds.Top == game.Top)
            : _s.Monitors.All.FirstOrDefault(_s.Bars.IsFullscreen);
        // «Монитор ПК»: правый клик по окну — меню оболочки, «Настройки…» — его страница.
        _s.Perf.OpenSettings = () => _s.OpenSettings("perf");
        _s.Perf.ShowMenu = (x, y, items) => _s.Menu.Show("perf-window", new Rect(x, y, 0, 0), items, Placement.AtPoint);
        _s.Keys.Combo += OnWinCombo;
        _s.Keys.Captured += (vk, shift) => { foreach (var d in _docks.Values) if (d.KeyboardActive) d.KeyboardKey(vk, shift); };
        _s.Keys.CaptureBroken += () => { foreach (var d in _docks.Values) d.EndKeyboard(); };
        // Док показывается на пару секунд при запуске: видно, что оболочка запустилась.
        foreach (var d in _docks.Values) _s.Bars.Reveal(d, holdMs: 2000);
    }

    /// Монитор системного хвоста: выбранный в меню трея, иначе основной.
    Screen TailScreen => _s.Monitors.ByName(_s.Config.Bars.Monitor) ?? _s.Monitors.Primary;

    /// Док на каждом подключённом мониторе; пропавшие мониторы — их окна убираются.
    void Sync()
    {
        var screens = _s.Monitors.All;
        var tail = TailScreen;
        foreach (var name in _docks.Keys.Where(n => screens.All(s => s.Name != n)).ToList())
        {
            _docks[name].Close();
            _docks.Remove(name);
        }
        foreach (var screen in screens)
        {
            var full = screen.Name == tail.Name;
            if (_docks.TryGetValue(screen.Name, out var dock)) dock.Move(screen, full);
            else
            {
                dock = new Dock.Dock(_s, screen, full);
                _docks[screen.Name] = dock;
                dock.Show();
            }
        }
        _s.Bars.Publish();
    }

    /// Прятать панель задач Windows или нет (настройки → «Система»).
    void ApplyTaskbar()
    {
        if (_s.Config.Tail.HideTaskbar && !_taskbarReturned) Taskbar.Hide(tray: _s.Tray.Running);
        // Leftover: спрятал прошлый процесс, упавший до выхода (сторож поднял нас, а прятать уже не надо).
        else if (Taskbar.Hidden || Taskbar.Leftover) Taskbar.Restore("выключено в настройках");
    }

    /// Значок корзины на рабочем столе прячется, пока работает оболочка (настройки → «Виджеты»).
    void ApplyDesktopBin()
    {
        if (_s.Config.HideDesktopBin) DesktopBin.Hide();
        else DesktopBin.Restore("выключено в настройках");
    }

    /// Новым окнам говорим «у тебя есть кнопка на панели задач»: тогда программы шлют нам
    /// полоску загрузки и значки поверх иконки (как Проводнику).
    void AnnounceButtons()
    {
        var windows = _s.Tracker.Windows.Select(w => w.Hwnd).ToList();
        _announced.IntersectWith(windows);
        var fresh = windows.Where(_announced.Add).ToList();
        if (fresh.Count > 0) _s.Tray.AnnounceButtons(fresh);
        _s.Buttons.Prune(_announced);
    }

    void OnConfigChanged()
    {
        Glass.Set(_s.Config.Tint);
        ApplyTaskbar();
        ApplyDesktopBin();
        ApplyKeys();
        // Сменили монитор хвоста — хвост переезжает; закрепили док — резерв места.
        Sync();
        _s.Bars.Update();
        ApplyWidgets();
        RegisterHotkeys();
    }

    /// Какие сочетания с Win наши (окно настроек → «Клавиши»).
    void ApplyKeys()
    {
        var k = _s.Config.Keys;
        _s.Keys.WinStart = k.WinStart;
        _s.Keys.WinX = k.WinX;
        _s.Keys.WinSearch = k.WinSearch;
        _s.Keys.DockKeys = k.DockKeys;
        _s.Keys.PrintScreen = _s.Shots.Enabled;
        _s.Keys.WinShiftS = _s.Shots.Enabled && _s.Shots.Settings.WinShiftS;
        _s.Keys.WinV = _s.Clips.Enabled;
    }

    void ApplyWidgets()
    {
        if (_s.Config.WidgetEnabled("media")) _s.Media.Start();
        else _s.Media.Stop();
        _s.Clips.Apply();
        _s.Charge.Apply();
        _s.Perf.Apply();
    }

    /// Горячие клавиши профилей звука и «следующее устройство».
    void RegisterHotkeys()
    {
        var keys = new List<(string, Action)>();
        if (_s.Config.WidgetEnabled("audio"))
        {
            var settings = _s.AudioSettings;
            foreach (var p in settings.Profiles)
                if (!string.IsNullOrEmpty(p.Hotkey)) { var profile = p; keys.Add((p.Hotkey, () => SoundFlyout.ApplyProfile(_s.Audio, profile))); }
            if (!string.IsNullOrEmpty(settings.NextOutputHotkey)) keys.Add((settings.NextOutputHotkey, _s.Audio.NextOutput));
        }
        // Аварийно вернуть панель задач Windows (раздел 6) — всегда.
        keys.Add(("Ctrl+Alt+Shift+T", () => _s.RestoreTaskbar()));
        var key = string.Join("|", keys.Select(k => k.Item1));
        if (key == _hotkeysKey) return;
        _hotkeysKey = key;
        _hotkeys.Set(keys);
    }

    /// Док монитора, где сейчас курсор (Пуск, Win+X, Win+S открываются там — раздел 9).
    Dock.Dock? CursorDock()
    {
        GetCursorPos(out var p);
        var screen = _s.Monitors.At(p.X, p.Y);
        return _docks.TryGetValue(screen.Name, out var d) ? d : _docks.Values.FirstOrDefault();
    }

    /// Пуск над кнопкой этого дока; клавиша Win (dock = null): открыт — закрыть, иначе открыть
    /// на мониторе с курсором.
    void ToggleStart(Dock.Dock? dock, bool search = false)
    {
        var start = _s.StartMenu;
        if (dock is null)
        {
            if (start.IsOpen)
            {
                if (search) start.FocusSearch();
                else start.Close(restoreFocus: true);
                return;
            }
            dock = CursorDock();
            if (dock is null) return;
            Popups.Current?.Close(restoreFocus: false);
            OpenStart(dock);
            return;
        }
        Popups.Toggle(("start", dock), () => OpenStart(dock));
    }

    void OpenStart(Dock.Dock dock)
    {
        foreach (var d in _docks.Values) d.EndKeyboard();
        // Док выезжает и стоит, пока Пуск открыт (3.1).
        _s.Bars.Reveal(dock, force: true);
        _s.StartMenu.Show(("start", dock), dock.PillPx, dock);
    }

    void ToggleTools(Dock.Dock? dock)
    {
        dock ??= CursorDock();
        if (dock is null) return;
        var key = ("tools", dock);
        Popups.Toggle(key, () =>
        {
            _s.Bars.Reveal(dock, force: true);
            _s.Menu.Show(key, dock.StartButtonPx, ToolsMenu(), owner: dock);
        });
    }

    void OnWinCombo(int vk, bool shift, bool ctrl)
    {
        switch (vk)
        {
            case 'X': ToggleTools(null); break;
            case 'S' when shift: _s.Shots.Snap(); break;
            case 'V': ToggleClips(); break;
            case 'S' or 'Q': ToggleStart(null, search: true); break;
            case 'T' or 'B':
                Popups.CloseAll();
                foreach (var d in _docks.Values) d.EndKeyboard();
                CursorDock()?.BeginKeyboard(tail: vk == 'B');
                break;
            case >= '1' and <= '9':
                if (Popups.Current is { IsOpen: true }) Popups.CloseAll();
                CursorDock()?.ClickIndex(vk - '0', newWindow: shift && !ctrl, admin: shift && ctrl);
                break;
        }
    }

    /// Win+V: окошко буфера у текстового курсора окна, где печатали. Если сейчас активно наше
    /// окошко (Пуск, меню), вставлять будем в окно, которое было до него.
    void ToggleClips()
    {
        var flyout = _s.ClipsFlyout;
        if (flyout.IsOpen) { flyout.Toggle(IntPtr.Zero); return; }
        var target = GetForegroundWindow();
        if (Popups.Current is { IsOpen: true } p && p.Hwnd == target) target = _s.Tracker.Foreground;
        foreach (var d in _docks.Values) d.EndKeyboard();
        flyout.Toggle(target);
    }

    /// Служебное меню (3.7): пункты как в Win+X Windows 11, буквы-ускорители справа.
    List<MenuItem> ToolsMenu()
    {
        var items = new List<MenuItem>
        {
            new() { Text = "Установленные приложения", Glyph = "\uE71D", Key = 'F', Invoke = () => Launcher.Open("ms-settings:appsfeatures") },
        };
        if (WindowsShell.Start.Power.HasBattery)
            items.Add(new() { Text = "Мобильность", Glyph = "\uE8EA", Key = 'B', Invoke = () => Launcher.Run("mblctr.exe") });
        items.AddRange(
        [
            new() { Text = "Электропитание", Glyph = "\uE7E8", Key = 'O', Invoke = () => Launcher.Open("ms-settings:powersleep") },
            new() { Text = "Просмотр событий", Glyph = "\uE7C3", Key = 'V', Invoke = () => Launcher.Run("eventvwr.msc") },
            new() { Text = "Система", Glyph = "\uE7F8", Key = 'Y', Invoke = () => Launcher.Open("ms-settings:about") },
            new() { Text = "Диспетчер устройств", Glyph = "\uE772", Key = 'M', Invoke = () => Launcher.Run("devmgmt.msc") },
            new() { Text = "Сетевые подключения", Glyph = "\uE968", Key = 'W', Invoke = () => Launcher.Open("ms-settings:network") },
            new() { Text = "Управление дисками", Glyph = "\uEDA2", Key = 'K', Invoke = () => Launcher.Run("diskmgmt.msc") },
            new() { Text = "Управление компьютером", Glyph = "\uE977", Key = 'G', Invoke = () => Launcher.Run("compmgmt.msc") },
            new() { Text = "Терминал", Glyph = "\uE756", Key = 'I', Invoke = () => Launcher.Run("wt.exe", fallback: "powershell.exe") },
            new() { Text = "Терминал (администратор)", Glyph = "\uE7EF", Key = 'A', Invoke = () => Launcher.Run("wt.exe", admin: true, fallback: "powershell.exe") },
            MenuItem.Sep,
            new() { Text = "Диспетчер задач", Glyph = "\uE9D9", Key = 'T', Invoke = () => Launcher.Run("taskmgr.exe") },
            new() { Text = "Параметры", Glyph = "\uE713", Key = 'N', Invoke = () => Launcher.Open("ms-settings:") },
            new() { Text = "Проводник", Glyph = "\uEC50", Key = 'E', Invoke = () => Launcher.Run("explorer.exe") },
            new() { Text = "Поиск", Glyph = "\uE721", Key = 'S', Invoke = () => ToggleStart(null, search: true) },
            new() { Text = "Выполнить", Glyph = "\uE7C5", Key = 'R', Invoke = Launcher.OpenRunDialog },
            MenuItem.Sep,
            new() { Text = "Завершение работы или выход из системы", Glyph = "\uE7E8", Key = 'U', Children = WindowsShell.Start.StartMenu.PowerMenu(signOut: true) },
            new() { Text = "Рабочий стол", Glyph = "\uE8FC", Key = 'D', Invoke = () => WinKeys.SendWinCombo('D') },
            MenuItem.Sep,
            // Настройки самой оболочки: после ухода верхней панели с ⚙ они здесь, в поиске Пуска и в трее.
            new() { Text = "Настройки оболочки", Glyph = "\uE713", Invoke = () => _s.OpenSettings(null) },
        ]);
        return items;
    }

    /// Меню значка в трее (раздел 5): частое под рукой, всё остальное — в окне настроек.
    List<MenuItem> TrayMenu()
    {
        var bars = _s.Config.Bars;
        var tail = TailScreen.Name;
        var screens = _s.Monitors.All.Select((m, i) =>
        {
            var name = m.Name;
            return new MenuItem
            {
                Text = $"Монитор {i + 1} — {m.Bounds.Width}×{m.Bounds.Height}, {m.Scale * 100:0} %{(m.Primary ? " (основной)" : "")}",
                Checked = name == tail,
                Invoke = () => _s.Config.Update(c => c.Bars.Monitor = name),
            };
        }).ToList();
        return
        [
            new() { Text = "Настройки…", Glyph = "", Invoke = () => _s.OpenSettings(null) },
            new() { Text = "Монитор для хвоста", Glyph = "", Children = screens },
            new() { Text = "Закрепить док", Checked = bars.DockPinned, Glyph = "", Invoke = () => _s.Config.Update(c => c.Bars.DockPinned = !c.Bars.DockPinned) },
            new()
            {
                Text = "Стекло",
                Glyph = "",
                Children =
                [
                    new() { Text = "Стекло (размытие)", Checked = !_s.Config.Tint, Invoke = () => _s.Config.Update(c => c.Appearance.Glass = "acrylic") },
                    new() { Text = "Тонировка", Checked = _s.Config.Tint, Invoke = () => _s.Config.Update(c => c.Appearance.Glass = "tint") },
                ],
            },
            MenuItem.Sep,
            new() { Text = "Перезапустить оболочку", Glyph = "", Invoke = Restart },
            new() { Text = "Выход", Glyph = "", Invoke = Quit },
        ];
    }

    void Shutdown()
    {
        _s.Keys.Stop();
        _s.Perf.Shutdown();
        Popups.CloseAll();
        _s.Bars.Shutdown();
        _tray?.Remove();
        // Сначала закрываем свой трей, потом возвращаем панель: программы отдадут иконки Проводнику.
        _s.Tray.Stop();
        // При перезапуске панель остаётся спрятанной: новый процесс сразу забирает её себе.
        if (!_restarting) Taskbar.Restore(_s.Config.Tail.HideTaskbar ? "выход" : "выход (не пряталась)");
        if (!_leavingForRestart) DesktopBin.Restore("выход");
    }

    /// Выход: резерв места и значок убираются, процесс завершается (Application.Exit оставлял
    /// окна на экране и процесс живым).
    public void Quit()
    {
        Watchdog.Leaving(Watchdog.Leave.Quit);
        Shutdown();
        Log.Write("выход");
        Environment.Exit(0);
    }

    void Restart()
    {
        // Новый процесс запускает сторож; его нет — запускаемся сами.
        var watchdog = Watchdog.Leaving(Watchdog.Leave.Restart);
        _restarting = Taskbar.Hidden;
        _leavingForRestart = true;
        Shutdown();
        Log.Write("перезапуск");
        if (!watchdog)
        {
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Watchdog.RestartArg) { UseShellExecute = false }); }
            catch (Exception e) { Log.Error("перезапуск", e); }
        }
        Environment.Exit(0);
    }
}
