using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Ui;
using static WindowsShell.Tail.FlyoutUi;

namespace WindowsShell.Tail;

/// Окошко часов (раздел 2.5, WSH-49): дата крупно и календарь на месяц. Сегодня выделено,
/// стрелки ‹ › и колесо листают месяцы. Календарь свой: у CalendarView на mephPC не рисовалась
/// строка дней недели.
public sealed class ClockFlyout : Popup
{
    const double CellSize = 40;

    readonly StackPanel _body = new() { Padding = new Thickness(16, 14, 16, 10), Spacing = 6 };
    readonly TextBlock _date = Label("", 15, bold: true);
    readonly TextBlock _month = Label("", 14, bold: true);
    readonly Grid _days = new();
    DateTime _shown;

    public ClockFlyout() : base(activating: true)
    {
        var head = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _month.Margin = new Thickness(4, 0, 0, 0);
        head.Children.Add(_month);
        var prev = Arrow("", -1);
        var next = Arrow("", 1);
        Grid.SetColumn(prev, 1);
        Grid.SetColumn(next, 2);
        head.Children.Add(prev);
        head.Children.Add(next);

        for (var i = 0; i < 7; i++) _days.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellSize) });
        _days.HorizontalAlignment = HorizontalAlignment.Center;
        _days.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            Step(e.GetCurrentPoint(_days).Properties.MouseWheelDelta > 0 ? -1 : 1);
        };

        _body.Children.Add(_date);
        _body.Children.Add(head);
        _body.Children.Add(_days);
        _body.Children.Add(Link("Настроить дату и время", () => { Close(restoreFocus: false); Launcher.Open("ms-settings:dateandtime"); }));
        SetContent(_body);
    }

    Button Arrow(string glyph, int step)
    {
        var b = new Button
        {
            Content = Icon(glyph, 12),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
        };
        b.Click += (_, _) => Step(step);
        return b;
    }

    protected override double Width => 332;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(width, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    public void Show(object anchor, Rect anchorPx, object? owner)
    {
        var now = DateTime.Now;
        var text = now.ToString("dddd, d MMMM yyyy");
        _date.Text = text.Length > 0 ? char.ToUpper(text[0]) + text[1..] : text;
        // Каждый раз открывается на текущем месяце.
        _shown = new DateTime(now.Year, now.Month, 1);
        Build();
        Open(anchor, anchorPx, owner: owner);
    }

    void Step(int months)
    {
        _shown = _shown.AddMonths(months);
        Build();
        Layout();
    }

    void Build()
    {
        var culture = CultureInfo.CurrentCulture;
        // «MMMM» без дня — именительный падеж: «Октябрь 2026».
        var month = _shown.ToString("MMMM yyyy", culture);
        _month.Text = month.Length > 0 ? char.ToUpper(month[0]) + month[1..] : month;

        _days.Children.Clear();
        _days.RowDefinitions.Clear();
        var first = culture.DateTimeFormat.FirstDayOfWeek;
        // Строка дней недели: «Пн Вт Ср …», начиная с первого дня недели страны.
        _days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        for (var i = 0; i < 7; i++)
        {
            var name = culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(((int)first + i) % 7));
            if (name.Length > 0) name = char.ToUpper(name[0]) + name[1..];
            var t = Label(name, 12, secondary: true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(t, i);
            _days.Children.Add(t);
        }

        var today = DateTime.Today;
        var offset = ((int)_shown.DayOfWeek - (int)first + 7) % 7;
        var start = _shown.AddDays(-offset);
        for (var r = 0; r < 6; r++)
        {
            _days.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellSize) });
            for (var c = 0; c < 7; c++)
            {
                var d = start.AddDays(r * 7 + c);
                var isToday = d == today;
                var inMonth = d.Month == _shown.Month;
                var t = Label(d.Day.ToString(), 14, secondary: !inMonth);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                if (isToday) t.Foreground = Theme.Brush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
                if (!inMonth) t.Opacity = 0.55;
                var cell = new Border
                {
                    Width = CellSize - 4,
                    Height = CellSize - 4,
                    CornerRadius = new CornerRadius((CellSize - 4) / 2),
                    Background = Theme.Brush(isToday ? Theme.Accent : Microsoft.UI.Colors.Transparent),
                    Child = t,
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                _days.Children.Add(cell);
            }
        }
    }
}
