using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Charge;

/// Плашка «пора зарядить» поверх полноэкранной игры (design-v2 п. 7): правый верхний угол
/// монитора с игрой, 5 секунд, фокус не забирает. Видна в оконных и
/// «полноэкранных» играх (оптимизация во весь экран Windows), не видна в эксклюзивном режиме.
public sealed class ChargeBanner
{
    const double W = 300, H = 64;
    const int ShowMs = 5000;

    readonly ShellWindow _win;
    readonly FontIcon _icon = new() { FontFamily = Theme.Icons, FontSize = 26, Foreground = Theme.Brush(Theme.Warning), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _title = new() { FontFamily = Theme.Font, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Text), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _text = new() { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextSecondary), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Border _bar = new() { Height = 3, Background = Theme.Brush(Theme.Warning), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
    readonly DispatcherQueueTimer _timer;
    long _shownAt;

    public ChargeBanner(DispatcherQueue queue)
    {
        _win = new ShellWindow(Surface.Menu, noActivate: true, rounded: true);
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 10, 16, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(_icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { _title, _text } };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        _win.Root.Children.Add(row);
        _win.Root.Children.Add(_bar);
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) => Tick();
    }

    /// `screen` — монитор с игрой; null — монитор с курсором.
    public void Show(ChargeDevice d, int percent, string text, Screen? screen)
    {
        _icon.Glyph = ChargeDevice.LevelGlyph(percent, false);
        _title.Text = $"{d.Name}: {percent} %";
        _text.Text = text;
        _bar.Width = W;
        RECT area;
        double scale;
        if (screen != null)
        {
            area = screen.Bounds;
            scale = screen.Scale;
        }
        else
        {
            GetCursorPos(out var p);
            var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
            area = MonitorInfo(mon).rcMonitor;
            scale = MonitorScale(mon);
        }
        var w = (int)Math.Ceiling(W * scale);
        var h = (int)Math.Ceiling(H * scale);
        var margin = (int)(24 * scale);
        _win.Place(area.Right - margin - w, area.Top + margin, w, h);
        _win.Show(activate: false);
        _win.Raise();
        _shownAt = Environment.TickCount64;
        _timer.Start();
    }

    void Tick()
    {
        var left = ShowMs - (Environment.TickCount64 - _shownAt);
        if (left <= 0)
        {
            _timer.Stop();
            _win.Hide();
            return;
        }
        _bar.Width = W * left / ShowMs;
        // Игра могла снова встать поверх (она тоже «поверх всех»): поднимаемся ещё раз.
        if ((Environment.TickCount64 - _shownAt) % 1000 < 40) _win.Raise();
    }
}
