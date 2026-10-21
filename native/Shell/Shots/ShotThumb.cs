using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Shots;

/// Миниатюра над доком после снимка или записи (design-v2 п. 4): видна 5 секунд, даже если док
/// спрятан. Полоска снизу показывает, сколько ещё; навёл курсор — не исчезает. Потянул — файл
/// перетаскивается в любое окно; клик — редактор (у записи — открыть); правый клик — убрать.
public sealed class ShotThumb
{
    const double W = 240;
    const int ShowMs = 5000;

    readonly ShellWindow _win;
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _caption = new() { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextSecondary), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _action = new() { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.Accent), VerticalAlignment = VerticalAlignment.Center };
    readonly Border _bar = new() { Height = 3, Background = Theme.Brush(Theme.Accent), HorizontalAlignment = HorizontalAlignment.Left };
    readonly Border _picture;
    readonly DispatcherQueueTimer _timer;
    ShotItem? _item;
    Rect _anchor;
    double _imageH = 135;
    long _shownAt;
    bool _hover, _pressed;
    Point _pressPoint;
    int _version;

    /// Клик по миниатюре.
    public event Action<ShotItem>? Clicked;

    public ShotThumb(DispatcherQueue queue)
    {
        _win = new ShellWindow(Surface.Menu, noActivate: true, rounded: true);
        _picture = new Border { Background = Theme.Brush(Windows.UI.Color.FromArgb(255, 0x20, 0x22, 0x26)), Child = _image };
        var meta = new Grid { Padding = new Thickness(10, 7, 10, 7), ColumnSpacing = 8 };
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        meta.Children.Add(_caption);
        Grid.SetColumn(_action, 1);
        meta.Children.Add(_action);
        var root = new StackPanel { Children = { _picture, meta, _bar } };
        _win.Root.Children.Add(root);
        _win.Root.PointerEntered += (_, _) => { _hover = true; _bar.Width = W; };
        _win.Root.PointerExited += (_, _) => { _hover = false; _shownAt = Environment.TickCount64; };
        _win.Root.PointerPressed += OnPressed;
        _win.Root.PointerMoved += OnMoved;
        _win.Root.PointerReleased += OnReleased;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) => Tick();
    }

    public bool Visible => _win.Visible;

    /// Показать над прямоугольником (значок «Снимки» в хвосте), физические пиксели.
    public async void Show(ShotItem item, string caption, Rect anchorPx)
    {
        var v = ++_version;
        _item = item;
        _anchor = anchorPx;
        _caption.Text = caption;
        _action.Text = item.IsImage ? "Изменить" : "Открыть";
        var scale = MonitorScale(MonitorFromPoint(new POINT { X = (int)(anchorPx.X + anchorPx.Width / 2), Y = (int)anchorPx.Y }, MONITOR_DEFAULTTONEAREST));
        var source = await ShotImages.Load(item, (int)(W * scale));
        if (v != _version) return;
        _image.Source = source;
        _imageH = 135;
        if (source is Microsoft.UI.Xaml.Media.Imaging.BitmapSource { PixelWidth: > 0 } b)
            _imageH = Math.Clamp(W * b.PixelHeight / b.PixelWidth, 60, 170);
        _picture.Height = _imageH;
        _image.Width = W;
        _bar.Width = W;
        _hover = false;
        _pressed = false;
        _shownAt = Environment.TickCount64;
        Place();
        _win.Show(activate: false);
        _win.Raise();
        _timer.Start();
    }

    void Place()
    {
        var mon = MonitorFromPoint(new POINT { X = (int)(_anchor.X + _anchor.Width / 2), Y = (int)_anchor.Y }, MONITOR_DEFAULTTONEAREST);
        var scale = MonitorScale(mon);
        var area = MonitorInfo(mon).rcMonitor;
        var w = (int)Math.Ceiling(W * scale);
        var h = (int)Math.Ceiling((_imageH + 34 + 3) * scale);
        var margin = (int)(8 * scale);
        var x = (int)(_anchor.X + _anchor.Width / 2 - w / 2.0);
        x = Math.Clamp(x, area.Left + margin, area.Right - margin - w);
        var y = Math.Max(area.Top + margin, (int)(_anchor.Y - 10 * scale - h));
        _win.Place(x, y, w, h);
    }

    void Tick()
    {
        if (!_win.Visible) { _timer.Stop(); return; }
        if (_hover || _pressed) return;
        var left = ShowMs - (Environment.TickCount64 - _shownAt);
        if (left <= 0) { Hide(); return; }
        _bar.Width = W * left / ShowMs;
    }

    public void Hide()
    {
        _timer.Stop();
        _version++;
        if (_win.Visible) _win.Hide();
        _image.Source = null;
    }

    /// Миниатюра этого файла (удалили — убрать).
    public bool Shows(string path) => _win.Visible && _item?.Path == path;

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_win.Root);
        if (p.Properties.IsRightButtonPressed) { Hide(); return; }
        if (!p.Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _pressPoint = p.Position;
        _win.Root.CapturePointer(e.Pointer);
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || _item is not { } item) return;
        var p = e.GetCurrentPoint(_win.Root);
        if (!p.Properties.IsLeftButtonPressed || Math.Abs(p.Position.X - _pressPoint.X) + Math.Abs(p.Position.Y - _pressPoint.Y) < 6) return;
        _pressed = false;
        _win.Root.ReleasePointerCaptures();
        // Пока тащим, миниатюра стоит; бросили — пропадает (своё дело сделала).
        if (FileDrag.Start(_win.Hwnd, item.Path)) Hide();
        else _shownAt = Environment.TickCount64;
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        _win.Root.ReleasePointerCaptures();
        if (e.GetCurrentPoint(_win.Root).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased || _item is not { } item) return;
        Hide();
        Clicked?.Invoke(item);
    }
}
