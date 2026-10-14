using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Ui;
using static WindowsShell.Tail.FlyoutUi;

namespace WindowsShell.Tail;

/// Окошко часов (раздел 2.5, WSH-49): дата крупно и календарь на месяц. Сегодня выделено,
/// стрелки ‹ › и колесо листают месяцы, клик по заголовку — выбор месяца и года (это умеет
/// сам CalendarView, как в Windows).
public sealed class ClockFlyout : Popup
{
    readonly StackPanel _body = new() { Padding = new Thickness(16, 14, 16, 10), Spacing = 6 };
    readonly TextBlock _date = Label("", 15, bold: true);
    readonly CalendarView _calendar;

    public ClockFlyout() : base(activating: true)
    {
        _calendar = new CalendarView
        {
            FirstDayOfWeek = (Windows.Globalization.DayOfWeek)(int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek,
            IsTodayHighlighted = true,
            SelectionMode = CalendarViewSelectionMode.None,
            BorderThickness = new Thickness(0),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CalendarIdentifier = Windows.Globalization.CalendarIdentifiers.Gregorian,
            Language = CultureInfo.CurrentUICulture.Name,
        };
        _body.Children.Add(_date);
        _body.Children.Add(_calendar);
        _body.Children.Add(Link("Настроить дату и время", () => { Close(restoreFocus: false); Launcher.Open("ms-settings:dateandtime"); }));
        SetContent(_body);
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
        _calendar.SetDisplayDate(now);
        Open(anchor, anchorPx, owner: owner);
    }
}
