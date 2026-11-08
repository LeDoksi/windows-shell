using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Config;
using WindowsShell.Settings;
using static WindowsShell.Island.IslandUi;

namespace WindowsShell.Island.Widgets;

/// Настройки-«лист» внутри острова (П14, эскиз Settings): самое большое состояние тела,
/// ≈65 % высоты экрана. Слева — разделы: виджеты по отдельности, «Остров» (лента и события,
/// перенос), «Оболочка» (страницы окна настроек). Справа — страница; изменения применяются
/// сразу. Страницы те же, что в окне настроек (`SettingsPages`), в цветах острова.
public sealed class SettingsWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    const double NavW = 196, SheetW = 860;

    static readonly (string Group, (string Id, string Title)[] Items)[] Nav =
    [
        ("ВИДЖЕТЫ", [("w-sound", "Звук"), ("w-media", "Медиа"), ("w-charge", "Заряд"), ("w-shots", "Снимки и запись"), ("w-bin", "Корзина и полка"), ("w-clips", "Буфер обмена")]),
        ("ОСТРОВ", [("i-ribbon", "Лента и события"), ("i-move", "Перенос")]),
        ("ОБОЛОЧКА", [("dock", "Док"), ("start", "Пуск"), ("look", "Внешний вид"), ("perf", "Монитор ПК"), ("kanban", "Канбан"), ("keys", "Клавиши"), ("system", "Система")]),
    ];

    static string _page = "i-ribbon";

    readonly StackPanel _pageBody = new() { Spacing = 0, Padding = new Thickness(4, 0, 14, 12) };
    readonly ScrollViewer _pageScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Dictionary<string, Button> _navButtons = [];
    SettingsPages? _pages;
    Grid? _grid;
    bool _self;
    double _height = 520;

    public override string Id => "settings";
    public override double Width => SheetW;

    public override (double W, double H)? Sheet(Native.Screen screen)
    {
        var h = (screen.Bounds.Bottom - screen.Bounds.Top) / screen.Scale;
        var w = (screen.Bounds.Right - screen.Bounds.Left) / screen.Scale;
        _height = Math.Max(420, Math.Round(h * 0.65));
        return (Math.Min(SheetW, w - 40), _height);
    }

    protected override void OnOpen()
    {
        S.Config.Changed += OnConfig;
        S.Kanban.Changed += OnKanban;
    }

    protected override void OnClose()
    {
        S.Config.Changed -= OnConfig;
        S.Kanban.Changed -= OnKanban;
    }

    void OnConfig() { if (!_self) S.Queue.TryEnqueue(BuildPage); }

    void OnKanban()
    {
        if (_page == "kanban" && _pages != null && _pages.KanbanState() != _pages.KanbanBuilt) S.Queue.TryEnqueue(BuildPage);
    }

    protected override void Build()
    {
        Root.RequestedTheme = ElementTheme.Dark;
        _pages ??= new SettingsPages(S, _pageBody, c =>
        {
            _self = true;
            try { S.Config.Update(c); }
            finally { _self = false; }
        }, BuildPage);

        // Прокрутку страницы переносим в новую сетку явно: отцепленная сетка не сообщает Parent
        // (winui-tree-gotchas), и Children.Add бросил бы COMException.
        _grid?.Children.Remove(_pageScroll);
        var grid = _grid = new Grid { ColumnSpacing = 14, Height = _height - RibbonView.Height - 26 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavW) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var nav = new StackPanel { Spacing = 1, Padding = new Thickness(6, 4, 6, 8) };
        _navButtons.Clear();
        foreach (var (group, items) in Nav)
        {
            nav.Children.Add(new Border { Padding = new Thickness(10, 10, 10, 4), Child = Text(group, 11, Faint, bold: true) });
            foreach (var (id, title) in items)
            {
                var b = new Button
                {
                    Content = Text(title, 13),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(10, 6, 10, 7),
                };
                var page = id;
                b.Click += (_, _) => Go(page);
                _navButtons[id] = b;
                nav.Children.Add(b);
            }
        }
        nav.Children.Add(new Border { Height = 10 });
        nav.Children.Add(Link("Окно настроек ↗", () => { Body.Sleep(restoreFocus: false); S.OpenSettings(null); }));
        var navBox = new Border
        {
            Background = B(Group),
            CornerRadius = new CornerRadius(16),
            Child = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(navBox, "Разделы настроек");
        grid.Children.Add(navBox);

        _pageScroll.Content = _pageBody;
        Grid.SetColumn(_pageScroll, 1);
        grid.Children.Add(_pageScroll);
        Root.Children.Add(grid);
        BuildPage();
    }

    void Go(string page)
    {
        if (_page == page) return;
        _page = page;
        BuildPage();
        _pageScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    void BuildPage()
    {
        if (!IsOpen || _pages is null) return;
        var offset = _pageScroll.VerticalOffset;
        var title = Nav.SelectMany(g => g.Items).FirstOrDefault(i => i.Id == _page).Title ?? "Настройки";
        foreach (var (id, b) in _navButtons)
        {
            b.Background = B(id == _page ? SelectedSlot : Microsoft.UI.Colors.Transparent);
            ((TextBlock)b.Content).FontWeight = id == _page ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        Rows.IslandStyle = true;
        try
        {
            _pageBody.Children.Clear();
            _pageBody.Children.Add(Rows.Title(title));
            _pages.Build(_page);
        }
        catch (Exception e) { Log.Error($"остров: настройки {_page}", e); }
        finally { Rows.IslandStyle = false; }
        S.Queue.TryEnqueue(() => _pageScroll.ChangeView(null, offset, null, disableAnimation: true));
    }
}
