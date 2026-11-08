using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WindowsShell.Config;
using WindowsShell.Native;
using WindowsShell.Perf;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using static WindowsShell.Settings.Rows;

namespace WindowsShell.Settings;

/// Окно настроек (WSH-39, раздел E8 списка проверки): док и хвост, Пуск, стекло,
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
        ("island", "Остров", "\uE737"),
        ("dock", "Док и хвост", ""),
        ("start", "Пуск", ""),
        ("look", "Внешний вид", ""),
        ("widgets", "Виджеты", ""),
        ("perf", "Монитор ПК", "\uE9D9"),
        ("kanban", "Канбан", "\uE9D5"),
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
    readonly SettingsPages _pages;

    SettingsWindow(Services services, string page)
    {
        _s = services;
        _page = page;
        _pages = new SettingsPages(services, _body, Change, Build);
        _window = new Window { Title = "Настройки — Windows Shell" };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        Apps.OwnWindows.Register(_hwnd, "settings", "Настройки", "\uE713");
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
        _window.AppWindow.SetPresenter(presenter);
        SetDark(_hwnd);
        try { _window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "shell.ico")); } catch { }
        Place();

        _s.Config.Changed += OnConfigChanged;
        _s.Monitors.Changed += OnMonitorsChanged;
        _s.Kanban.Changed += OnKanbanChanged;
        _window.Closed += (_, _) =>
        {
            _s.Config.Changed -= OnConfigChanged;
            _s.Monitors.Changed -= OnMonitorsChanged;
            _s.Kanban.Changed -= OnKanbanChanged;
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

    /// Страница канбана перестраивается, только когда вошли, вышли или ждём браузер
    /// (не на каждое чтение задач: поле пароля не должно сбрасываться).
    void OnKanbanChanged()
    {
        if (_page == "kanban" && _pages.KanbanState() != _pages.KanbanBuilt) Build();
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
        _pages.Build(_page);
        if (samePage) _scroll.DispatcherQueue.TryEnqueue(() => _scroll.ChangeView(null, offset, null, disableAnimation: true));
        else _scroll.ChangeView(null, 0, null, disableAnimation: true);
    }


}
