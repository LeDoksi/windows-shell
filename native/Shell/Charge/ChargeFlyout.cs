using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using static WindowsShell.Tail.FlyoutUi;

namespace WindowsShell.Charge;

/// Окошко «Заряд устройств» над значком в хвосте (design-v2 п. 7): все устройства списком —
/// значок, имя, полоска и %, «заряжается», «не подключено»; севшие оранжевые. Правый клик
/// по устройству — «Не следить за этим устройством».
public sealed class ChargeFlyout : Popup
{
    readonly ChargeService _charge;
    readonly MenuPopup _menu = new();
    readonly StackPanel _body = new() { Padding = new Thickness(16, 14, 16, 10), Spacing = 2 };

    /// Открыть настройки виджета (ссылка внизу).
    public Action OpenSettings { get; set; } = () => { };

    public ChargeFlyout(ChargeService charge) : base(activating: true)
    {
        _charge = charge;
        SetContent(_body);
        _charge.Changed += () => { if (IsOpen) { Build(); Layout(); } };
    }

    protected override double Width => 340;
    protected override double Gap => 8;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(width, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    public void Show(object anchor, Rect anchorPx, object owner)
    {
        Build();
        Open(anchor, anchorPx, Placement.Above, owner: owner);
        _charge.Refresh();
    }

    void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(Label("Заряд устройств", 15, bold: true));
        var list = _charge.Watched.ToList();
        var hidden = _charge.Settings.Ignored.Count;
        _body.Children.Add(Label(Summary(list, hidden), 12, secondary: true));
        _body.Children.Add(Separator());
        if (list.Count == 0)
            _body.Children.Add(new TextBlock
            {
                Text = "Устройства с батареей появятся здесь сами: Bluetooth-клавиатуры, мыши, геймпады, наушники и мыши и гарнитуры Logitech.",
                FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.TextSecondary),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 10),
            });
        foreach (var d in list) _body.Children.Add(Row(d));
        _body.Children.Add(Separator());
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        links.Children.Add(Link("Bluetooth и устройства", () => { Close(restoreFocus: false); Launcher.Open("ms-settings:bluetooth"); }));
        links.Children.Add(Link("Настройки", () => { Close(restoreFocus: false); OpenSettings(); }));
        _body.Children.Add(links);
    }

    string Summary(List<ChargeDevice> list, int hidden)
    {
        var low = list.Count(_charge.IsLow);
        var text = list.Count == 0 ? "Пока ничего не найдено" : low > 0 ? $"Пора зарядить: {low}" : "Всё заряжено";
        return hidden > 0 ? $"{text} · скрыто {hidden}" : text;
    }

    UIElement Row(ChargeDevice d)
    {
        var low = _charge.IsLow(d);
        var color = low ? Theme.Warning : Theme.Text;
        var icon = Icon(d.Glyph, 20);
        icon.Foreground = Theme.Brush(d.Connected ? color : Theme.TextSecondary);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var name = Label(d.Name, 13);
        if (!d.Connected) name.Foreground = Theme.Brush(Theme.TextSecondary);
        var status = d switch
        {
            { Connected: false } => "не подключено",
            { Charging: true } => "заряжается",
            { Percent: null } => "заряд неизвестен",
            _ when low => "пора зарядить",
            _ => null,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 5 };
        text.Children.Add(name);
        if (d.Percent is { } p)
        {
            var track = new Grid { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Brush(Theme.Separator) };
            track.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = Theme.Brush(low ? Theme.Warning : d.Charging ? Theme.Accent : Theme.Text),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(4, 200 * p / 100.0),
            });
            track.Width = 200;
            track.HorizontalAlignment = HorizontalAlignment.Left;
            text.Children.Add(track);
        }
        var sub = string.Join(" · ", new[] { d.Connected ? d.Detail : null, status }.Where(x => !string.IsNullOrEmpty(x)));
        if (sub.Length > 0)
        {
            var s = Label(sub, 11, secondary: true);
            if (low) s.Foreground = Theme.Brush(Theme.Warning);
            text.Children.Add(s);
        }

        var percent = Label(d.Percent is { } v ? $"{v} %" : "", 13);
        percent.Foreground = Theme.Brush(color);
        percent.HorizontalAlignment = HorizontalAlignment.Right;

        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 8, 8, 8), CornerRadius = new CornerRadius(6), Background = Theme.Brush(Microsoft.UI.Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(percent, 2);
        row.Children.Add(percent);
        ToolTipService.SetToolTip(row, d.Source.Length > 0 ? $"{d.Title}\nДанные: {d.Source}" : d.Title);
        row.PointerEntered += (_, _) => row.Background = Theme.Brush(Theme.Hover);
        row.PointerExited += (_, _) => row.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
        row.RightTapped += (_, e) => { e.Handled = true; RowMenu(d, row, e); };
        return row;
    }

    void RowMenu(ChargeDevice d, FrameworkElement row, RightTappedRoutedEventArgs e)
    {
        var at = e.GetPosition(null);
        var items = new List<MenuItem>
        {
            new() { Text = "Не следить за этим устройством", Glyph = "", Invoke = () => _charge.Ignore(d) },
        };
        var scale = Win.Scale;
        GetWindowRect(Hwnd, out var r);
        _menu.ShowInside(this, ("charge", d.Key), new Rect(r.Left + at.X * scale, r.Top + at.Y * scale, 1, 1), items);
    }
}
