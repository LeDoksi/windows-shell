using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Ui;
using static WindowsShell.Tail.FlyoutUi;

namespace WindowsShell.Tail;

/// Окошко корзины над значком в хвосте (design-v2 п. 3): последние 10 удалённых с кнопкой
/// «Восстановить» при наведении, внизу «Открыть корзину» и «Очистить» с подтверждением тут же.
public sealed class BinFlyout : Popup
{
    const int Shown = 10;
    readonly RecycleBin _bin;
    readonly Icons _icons;
    readonly StackPanel _body = new() { Padding = new Thickness(16, 14, 16, 12), Spacing = 4 };
    List<BinItem> _recent = [];
    bool _confirm;
    string? _error;
    int _version;

    public BinFlyout(RecycleBin bin, Icons icons) : base(activating: true)
    {
        _bin = bin;
        _icons = icons;
        SetContent(_body);
        _bin.Changed += () => { if (IsOpen) Load(); };
    }

    protected override double Width => 360;
    protected override double Gap => 8;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(width, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    /// `confirm` — сразу спросить «Удалить навсегда?» (правый клик → «Очистить корзину»).
    public void Show(object anchor, Rect anchorPx, object owner, bool confirm = false)
    {
        _confirm = confirm && !_bin.Empty;
        _error = null;
        Build();
        Open(anchor, anchorPx, Placement.Above, owner: owner);
        Load();
    }

    /// Список читаем в фоне: в большой корзине файлов `$I…` тысячи.
    void Load()
    {
        var v = ++_version;
        var queue = Win.Window.DispatcherQueue;
        Task.Run(() =>
        {
            var list = _bin.Recent(Shown);
            queue.TryEnqueue(() =>
            {
                if (v != _version || !IsOpen) return;
                _recent = list;
                if (_bin.Empty) _confirm = false;
                Build();
                Layout();
            });
        });
    }

    void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(Label("Корзина", 15, bold: true));
        _body.Children.Add(Label(_bin.Empty ? "Пусто" : _bin.Summary, 12, secondary: true));
        _body.Children.Add(Separator());

        if (_recent.Count == 0)
            _body.Children.Add(new TextBlock
            {
                Text = _bin.Empty ? "Здесь появится то, что удалишь" : "…",
                FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 10),
            });
        foreach (var item in _recent) _body.Children.Add(Row(item));
        if (_error != null) _body.Children.Add(new TextBlock { Text = _error, FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.Danger), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 0) });

        _body.Children.Add(Separator());
        _body.Children.Add(_confirm ? Confirm() : Footer());
    }

    UIElement Row(BinItem item)
    {
        var icon = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        var fallback = Icon(item.Folder ? "" : "", 18);
        fallback.VerticalAlignment = VerticalAlignment.Center;
        var iconBox = new Grid { Width = 24, Height = 24, Children = { fallback, icon } };
        var scale = Win.Window.Content?.XamlRoot?.RasterizationScale ?? 1;
        _icons.Get(item.DataPath, (int)Math.Round(24 * scale), bmp =>
        {
            icon.Source = bmp;
            fallback.Visibility = bmp is null ? Visibility.Visible : Visibility.Collapsed;
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(Label(item.Name, 13));
        var where = Path.GetDirectoryName(item.OriginalPath);
        text.Children.Add(Label($"{When(item.Deleted)} · {where}", 11, secondary: true));
        ToolTipService.SetToolTip(text, item.OriginalPath);

        var restore = new Button
        {
            Content = Label("Восстановить", 12),
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(5),
            Background = Theme.Brush(Theme.Card),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };
        restore.Click += (_, _) =>
        {
            if (_bin.Restore(item)) { _recent.Remove(item); _error = null; }
            else _error = $"Не получилось восстановить «{item.Name}». Открой корзину и восстанови оттуда.";
            Build();
            Layout();
        };
        // Кнопка видна при наведении на строку или когда до неё дошли клавишей Tab.
        restore.GotFocus += (_, _) => restore.Opacity = 1;
        restore.LostFocus += (_, _) => restore.Opacity = 0;

        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 5, 6, 5), CornerRadius = new CornerRadius(6), Background = Theme.Brush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(iconBox);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(restore, 2);
        row.Children.Add(restore);
        row.PointerEntered += (_, _) => { row.Background = Theme.Brush(Theme.Hover); restore.Opacity = 1; };
        row.PointerExited += (_, _) => { row.Background = Theme.Brush(Microsoft.UI.Colors.Transparent); if (restore.FocusState == FocusState.Unfocused) restore.Opacity = 0; };
        return row;
    }

    UIElement Footer()
    {
        var open = Action("Открыть корзину", Theme.Text);
        open.Click += (_, _) => { Close(restoreFocus: false); RecycleBin.Open(); };
        var clear = Action("Очистить", Theme.Danger);
        clear.IsEnabled = !_bin.Empty;
        clear.Click += (_, _) => { _confirm = true; Build(); Layout(); };
        return Buttons(open, clear);
    }

    UIElement Confirm()
    {
        var question = new TextBlock
        {
            Text = $"Удалить навсегда {RecycleBin.Objects(_bin.Count)} ({RecycleBin.SizeText(_bin.Size)})?",
            FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 6),
        };
        var delete = Action("Удалить", Theme.Danger);
        delete.Click += (_, _) => { Close(restoreFocus: true); _bin.EmptyAll(); };
        var cancel = Action("Отмена", Theme.Text);
        cancel.Click += (_, _) => { _confirm = false; Build(); Layout(); };
        var panel = new StackPanel { Children = { question, Buttons(delete, cancel) } };
        panel.Loaded += (_, _) => cancel.Focus(FocusState.Programmatic);
        return panel;
    }

    static Button Action(string text, Windows.UI.Color color) => new()
    {
        Content = new TextBlock { Text = text, FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(color) },
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Height = 34,
        CornerRadius = new CornerRadius(6),
        Background = Theme.Brush(Theme.Card),
        BorderThickness = new Thickness(0),
    };

    static Grid Buttons(Button left, Button right)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(left);
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    /// «только что», «5 мин назад», «3 ч назад», «вчера в 14:30», «12.10 в 14:30».
    public static string When(DateTime t)
    {
        var ago = DateTime.Now - t;
        if (ago.TotalMinutes < 1) return "только что";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} мин назад";
        if (t.Date == DateTime.Today) return $"{(int)ago.TotalHours} ч назад";
        if (t.Date == DateTime.Today.AddDays(-1)) return $"вчера в {t:HH:mm}";
        return t.Year == DateTime.Now.Year ? $"{t:dd.MM} в {t:HH:mm}" : $"{t:dd.MM.yyyy}";
    }
}
