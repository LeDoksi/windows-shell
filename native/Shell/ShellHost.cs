using System.Diagnostics;
using Windows.Foundation;
using WindowsShell.Audio;
using WindowsShell.Native;
using WindowsShell.Panel;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell;

/// Оболочка целиком: док и верхняя панель на каждом мониторе (раздел 9), значок в трее с
/// меню (раздел 5), горячие клавиши звука, стекло. Следит за мониторами и конфигом.
public sealed class ShellHost
{
    readonly Services _s;
    readonly Dictionary<string, Dock.Dock> _docks = [];
    readonly Dictionary<string, TopPanel> _panels = [];
    readonly Hotkeys _hotkeys;
    TrayIcon? _tray;
    WidgetSettingsFlyout? _widgetSettings;
    string _hotkeysKey = "";

    public ShellHost(Services services)
    {
        _s = services;
        _hotkeys = new Hotkeys(services.Messages);
        _s.OpenStart = OpenNativeStart;
        _s.OpenWidgetSettings = () => OpenWidgetSettings(null);
        _s.ToggleWidgetSettings = ToggleWidgetSettings;
        _s.RenameCategory = id =>
        {
            // Поле появляется под пилюлей на мониторе, где курсор.
            GetCursorPos(out var p);
            var screen = _s.Monitors.At(p.X, p.Y);
            if (_panels.TryGetValue(screen.Name, out var panel))
            {
                _s.Bars.Reveal(panel);
                panel.Rename(id);
            }
        };
    }

    public void Start()
    {
        Glass.Set(_s.Config.Tint);
        Sync();
        _tray = new TrayIcon(_s.Messages, _s.Menu, TrayMenu);
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
        _s.Messages.Message += (msg, _, _) =>
        {
            // Windows завершает работу: убираем резерв места и значок.
            if (msg == 0x0016 /* WM_ENDSESSION */) Shutdown();
        };
        ApplyWidgets();
        RegisterHotkeys();
        // Док показывается на пару секунд при запуске: видно, что оболочка запустилась.
        foreach (var d in _docks.Values) _s.Bars.Reveal(d, holdMs: 2000);
    }

    /// Монитор системного хвоста: выбранный в меню трея, иначе основной.
    Screen TailScreen => _s.Monitors.ByName(_s.Config.Bars.Monitor) ?? _s.Monitors.Primary;

    /// Док и панель на каждом подключённом мониторе; пропавшие мониторы — их окна убираются.
    void Sync()
    {
        var screens = _s.Monitors.All;
        var tail = TailScreen;
        foreach (var name in _docks.Keys.Where(n => screens.All(s => s.Name != n)).ToList())
        {
            _docks[name].Close();
            _docks.Remove(name);
        }
        foreach (var name in _panels.Keys.Where(n => screens.All(s => s.Name != n)).ToList())
        {
            _panels[name].Close();
            _panels.Remove(name);
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
            if (_panels.TryGetValue(screen.Name, out var panel)) panel.Move(screen);
            else
            {
                panel = new TopPanel(_s, screen);
                _panels[screen.Name] = panel;
                panel.Show();
            }
        }
        _s.Bars.Publish();
    }

    void OnConfigChanged()
    {
        Glass.Set(_s.Config.Tint);
        // Сменили монитор хвоста — хвост переезжает; закрепили док или панель — резерв места.
        Sync();
        _s.Bars.Update();
        ApplyWidgets();
        RegisterHotkeys();
    }

    void ApplyWidgets()
    {
        if (_s.Config.WidgetEnabled("media")) _s.Media.Start();
        else _s.Media.Stop();
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
        var key = string.Join("|", keys.Select(k => k.Item1));
        if (key == _hotkeysKey) return;
        _hotkeysKey = key;
        _hotkeys.Set(keys);
    }

    /// Свой Пуск будет в WSH-E15; пока кнопка открывает родной.
    static void OpenNativeStart()
    {
        keybd_event(VK_LWIN, 0, 0, IntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    void OpenWidgetSettings(Rect? anchor)
    {
        _widgetSettings ??= new WidgetSettingsFlyout(_s);
        if (anchor is { } a) Popups.Toggle(_widgetSettings, () => _widgetSettings.Show(a, null));
        else
        {
            // Из меню трея: окошко у курсора.
            GetCursorPos(out var p);
            _widgetSettings.Show(new Rect(p.X, p.Y, 0, 0), null);
        }
    }

    public void ToggleWidgetSettings(Rect anchor, object owner)
    {
        _widgetSettings ??= new WidgetSettingsFlyout(_s);
        Popups.Toggle(_widgetSettings, () => _widgetSettings.Show(anchor, owner));
    }

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
            new() { Text = "Настройки виджетов…", Glyph = "", Invoke = () => _s.OpenWidgetSettings() },
            new() { Text = "Монитор для хвоста", Glyph = "", Children = screens },
            new() { Text = "Закрепить док", Checked = bars.DockPinned, Glyph = "", Invoke = () => _s.Config.Update(c => c.Bars.DockPinned = !c.Bars.DockPinned) },
            new() { Text = "Закрепить панель", Checked = bars.LauncherPinned, Glyph = "", Invoke = () => _s.Config.Update(c => c.Bars.LauncherPinned = !c.Bars.LauncherPinned) },
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
        Popups.CloseAll();
        _s.Bars.Shutdown();
        _tray?.Remove();
    }

    /// Выход: резерв места и значок убираются, процесс завершается (Application.Exit оставлял
    /// окна на экране и процесс живым).
    public void Quit()
    {
        Shutdown();
        Log.Write("выход");
        Environment.Exit(0);
    }

    void Restart()
    {
        Shutdown();
        Log.Write("перезапуск");
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false }); }
        catch (Exception e) { Log.Error("перезапуск", e); }
        Environment.Exit(0);
    }
}
