using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using WindowsShell.Ui;
using VirtualKey = Windows.System.VirtualKey;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Tray;

/// Окошко ⌃ «Фоновые приложения» (раздел 2.1, WSH-47): иконки трея сеткой, как в Windows 11.
/// Клики передаются программам как в Windows. Пока открыто меню программы, окошко стоит; меню
/// закрылось или программа открыла своё окно — окошко закрывается само.
public sealed class TrayFlyout : Popup
{
    const double Cell = 40, IconSize = 16, Pad = 6;
    const int Columns = 5;

    readonly TrayHost _host;
    readonly Grid _grid = new();
    readonly TextBlock _tip;
    readonly StackPanel _body;
    readonly DispatcherQueueTimer _watch;
    readonly Dictionary<(IntPtr, uint), WriteableBitmap> _bitmaps = [];

    // Последний клик: по какой программе и когда — её меню и окна не закрывают окошко сразу.
    uint _clickedPid;
    long _clickedAt;
    bool _menuSeen;
    (IntPtr, uint, TrayHost.Button, long) _lastDown;
    TrayItem? _hovered;

    public TrayFlyout(TrayHost host) : base(activating: true)
    {
        _host = host;
        _tip = new TextBlock
        {
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.Brush(Theme.TextSecondary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 2, 6, 2),
            Height = 18,
        };
        _body = new StackPanel { Padding = new Thickness(Pad), Children = { _grid, _tip } };
        _grid.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        SetContent(_body);
        _host.Changed += () => { if (IsOpen) { Build(); Layout(); } };
        _watch = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _watch.Interval = TimeSpan.FromMilliseconds(150);
        _watch.Tick += (_, _) => Watch();
    }

    protected override bool KeepOpenWhenDeactivated => true;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    public void Show(object anchor, Rect anchorPx, object? owner)
    {
        _host.Prune();
        _menuSeen = false;
        _clickedPid = 0;
        Build();
        Open(anchor, anchorPx, owner: owner);
        _watch.Start();
    }

    protected override void OnClosing()
    {
        _watch.Stop();
        if (_hovered is { } h) { GetCursorPos(out var p); TrayHost.Hover(h, enter: false, p.X, p.Y); _hovered = null; }
    }

    void Build()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        var items = _host.Items.Where(i => !i.Hidden).ToList();
        _tip.Text = items.Count == 0 ? "Фоновых программ нет" : "";
        var cols = Math.Clamp(items.Count, 1, Columns);
        for (var c = 0; c < cols; c++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Cell) });
        for (var r = 0; r < (items.Count + cols - 1) / cols; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Cell) });
        var alive = new HashSet<(IntPtr, uint)>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            alive.Add((it.Hwnd, it.Uid));
            var cell = MakeCell(it);
            Grid.SetRow(cell, i / cols);
            Grid.SetColumn(cell, i % cols);
            _grid.Children.Add(cell);
        }
        foreach (var k in _bitmaps.Keys.Where(k => !alive.Contains(k)).ToList()) _bitmaps.Remove(k);
    }

    WriteableBitmap? Bitmap(TrayItem it)
    {
        if (it.Icon is not { } px) return null;
        var key = (it.Hwnd, it.Uid);
        // Картинку пересоздаём, только если иконка поменялась (мигание, счётчик).
        if (_bitmaps.TryGetValue(key, out var bmp) && bmp.PixelWidth == px.Width && bmp.PixelHeight == px.Height && ReferenceEquals(bmp.GetValue(SourceTag), px))
            return bmp;
        bmp = ToBitmap(px);
        bmp.SetValue(SourceTag, px);
        _bitmaps[key] = bmp;
        return bmp;
    }

    static readonly DependencyProperty SourceTag = DependencyProperty.RegisterAttached("TraySource", typeof(object), typeof(TrayFlyout), new PropertyMetadata(null));

    public static WriteableBitmap ToBitmap(TrayPixels px)
    {
        var bmp = new WriteableBitmap(px.Width, px.Height);
        using (var s = bmp.PixelBuffer.AsStream()) s.Write(px.Data, 0, px.Data.Length);
        bmp.Invalidate();
        return bmp;
    }

    FrameworkElement MakeCell(TrayItem it)
    {
        UIElement icon = Bitmap(it) is { } bmp
            ? new Image { Source = bmp, Width = IconSize, Height = IconSize, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform }
            : new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = IconSize, Foreground = Theme.Brush(Theme.TextSecondary) };
        var bg = new Border { CornerRadius = new CornerRadius(4), Margin = new Thickness(2), Background = Theme.Brush(Microsoft.UI.Colors.Transparent), Child = icon };
        ((FrameworkElement)icon).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)icon).VerticalAlignment = VerticalAlignment.Center;
        var cell = new ContentControl
        {
            Content = bg,
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        cell.PointerEntered += (_, _) =>
        {
            bg.Background = Theme.Brush(Theme.Hover);
            _tip.Text = it.Tip.Split('\n')[0];
            _hovered = it;
            var (x, y) = Center(cell);
            TrayHost.Hover(it, enter: true, x, y);
        };
        cell.PointerExited += (_, _) =>
        {
            bg.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
            _tip.Text = "";
            if (_hovered == it) _hovered = null;
            var (x, y) = Center(cell);
            TrayHost.Hover(it, enter: false, x, y);
        };
        cell.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            var pt = e.GetCurrentPoint(cell).Properties;
            var button = pt.IsRightButtonPressed ? TrayHost.Button.Right : pt.IsMiddleButtonPressed ? TrayHost.Button.Middle : TrayHost.Button.Left;
            cell.CapturePointer(e.Pointer);
            bg.Background = Theme.Brush(Theme.Pressed);
            var now = Environment.TickCount64;
            var dbl = _lastDown.Item1 == it.Hwnd && _lastDown.Item2 == it.Uid && _lastDown.Item3 == button && now - _lastDown.Item4 <= GetDoubleClickTime();
            _lastDown = (it.Hwnd, it.Uid, button, dbl ? 0 : now);
            NoteClick(it);
            var (x, y) = Center(cell);
            TrayHost.Down(it, button, dbl, x, y);
        };
        cell.PointerReleased += (_, e) =>
        {
            e.Handled = true;
            bg.Background = Theme.Brush(Theme.Hover);
            var kind = e.GetCurrentPoint(cell).Properties.PointerUpdateKind;
            cell.ReleasePointerCaptures();
            var button = kind switch
            {
                PointerUpdateKind.RightButtonReleased => TrayHost.Button.Right,
                PointerUpdateKind.MiddleButtonReleased => TrayHost.Button.Middle,
                _ => TrayHost.Button.Left,
            };
            NoteClick(it);
            var (x, y) = Center(cell);
            TrayHost.Up(it, button, x, y);
        };
        cell.KeyDown += (_, e) =>
        {
            var (x, y) = Center(cell);
            switch (e.Key)
            {
                case VirtualKey.Enter or VirtualKey.Space:
                    e.Handled = true;
                    NoteClick(it);
                    TrayHost.Down(it, TrayHost.Button.Left, false, x, y);
                    TrayHost.Up(it, TrayHost.Button.Left, x, y);
                    break;
                case VirtualKey.Application:
                case VirtualKey.F10 when KeyDown(VK_SHIFT):
                    e.Handled = true;
                    NoteClick(it);
                    TrayHost.Down(it, TrayHost.Button.Right, false, x, y);
                    TrayHost.Up(it, TrayHost.Button.Right, x, y);
                    break;
            }
        };
        return cell;
    }

    /// Центр клетки на экране (физические пиксели): программы ставят там своё меню.
    (int X, int Y) Center(FrameworkElement cell)
    {
        try
        {
            var p = cell.TransformToVisual(null).TransformPoint(new Point(Cell / 2, Cell / 2));
            GetWindowRect(Hwnd, out var r);
            var s = Win.Scale;
            return (r.Left + (int)(p.X * s), r.Top + (int)(p.Y * s));
        }
        catch
        {
            GetCursorPos(out var c);
            return (c.X, c.Y);
        }
    }

    IntPtr _raisedMenu;

    void NoteClick(TrayItem it)
    {
        _clickedPid = ProcessId(it.Hwnd);
        _clickedAt = Environment.TickCount64;
        _menuSeen = false;
    }

    /// Окошко не закрывается само, когда теряет фокус (так меню программы может открыться).
    /// Решаем здесь: пока открыто меню — стоим; меню закрылось или ушли в другое окно — закрываемся.
    void Watch()
    {
        if (!IsOpen) { _watch.Stop(); return; }
        var fg = GetForegroundWindow();
        var own = (uint)Environment.ProcessId;
        if (fg == Hwnd || fg != IntPtr.Zero && ProcessId(fg) == own) { _menuSeen = false; return; }
        var menu = FindWindowW("#32768", null);
        var fgPid = fg == IntPtr.Zero ? 0 : ProcessId(fg);
        var programMenu = menu != IntPtr.Zero && IsWindowVisible(menu)
            || _clickedPid != 0 && fgPid == _clickedPid && !IsRealWindow(fg);
        if (programMenu)
        {
            _menuSeen = true;
            // Наше окошко тоже поверх всех: меню программы поднимаем над ним, иначе оно под нами.
            var shown = menu != IntPtr.Zero && IsWindowVisible(menu) ? menu : fg;
            if (shown != IntPtr.Zero && shown != _raisedMenu)
            {
                _raisedMenu = shown;
                // Поднять чужое меню над нашим окном Windows не даёт: опускаем своё окошко из
                // «поверх всех», пока меню открыто.
                SetWindowPos(Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                SetWindowPos(Hwnd, shown, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            return;
        }
        if (_raisedMenu != IntPtr.Zero)
        {
            _raisedMenu = IntPtr.Zero;
            SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        if (_menuSeen) { Close(restoreFocus: false); return; }
        // Только что кликнули: программа ещё выводит своё меню (её скрытое окно уже впереди).
        if (_clickedPid != 0 && Environment.TickCount64 - _clickedAt < 1500 && (fgPid == _clickedPid || fg == IntPtr.Zero) && !IsRealWindow(fg)) return;
        Close(restoreFocus: false);
    }

    /// Обычное окно программы (а не меню, подсказка или скрытое служебное окно).
    static bool IsRealWindow(IntPtr h)
    {
        if (h == IntPtr.Zero || !IsWindowVisible(h)) return false;
        var cls = ClassName(h);
        if (cls is "Progman" or "WorkerW") return true;
        if ((ExStyle(h) & WS_EX_TOOLWINDOW) != 0) return false;
        const long WS_CAPTION = 0x00C00000;
        return ((long)GetWindowLongPtrW(h, GWL_STYLE) & WS_CAPTION) == WS_CAPTION || (ExStyle(h) & WS_EX_APPWINDOW) != 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindowW(string cls, string? title);
}
