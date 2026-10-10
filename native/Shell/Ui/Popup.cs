using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Ui;

/// Открыто только одно всплывающее окошко (правило 0.3); повторный клик по той же кнопке
/// закрывает его (0.2), а клик, который только что закрыл окошко, не открывает его снова.
public static class Popups
{
    public static Popup? Current { get; internal set; }
    static object? _lastAnchor;
    static long _lastClosed;

    internal static void NoteClosed(object? anchor)
    {
        _lastAnchor = anchor;
        _lastClosed = Environment.TickCount64;
    }

    /// Окошко этой кнопки закрылось только что — тем же нажатием, что пришло сейчас.
    public static bool JustClosed(object anchor) =>
        Equals(_lastAnchor, anchor) && Environment.TickCount64 - _lastClosed < 350;

    /// Переключить окошко кнопки: открыто — закрыть, закрыто — открыть (закрыв другое).
    public static void Toggle(object anchor, Action open)
    {
        if (Current is { } p && Equals(p.Anchor, anchor)) { p.Close(restoreFocus: true); return; }
        if (JustClosed(anchor)) return;
        Current?.Close(restoreFocus: false);
        open();
    }

    public static void CloseAll(bool restoreFocus = false) => Current?.Close(restoreFocus);
}

/// Всплывающее окошко с настоящим стеклом (меню, окошко звука, список окон). Стоит над кнопкой,
/// выровнено по её центру и не выходит за край экрана.
public abstract class Popup
{
    protected readonly ShellWindow Win;
    readonly bool _activating;
    readonly ContentControl _host;
    readonly CompositeTransform _shift = new();
    Rect _anchorPx;
    bool _beside;
    bool _closing;
    bool _wasActive;

    /// Кто открыл окошко (иконка дока, кнопка звука).
    public object? Anchor { get; private set; }
    public bool IsOpen => Win.Visible && !_closing;
    public bool Activating => _activating;
    public IntPtr Hwnd => Win.Hwnd;

    /// Отступ окошка от кнопки (DIP).
    protected virtual double Gap => 10;
    protected virtual double Width => 0;

    /// Закрыть и вернуть фокус прежнему окну.
    public event Action? Closed;

    /// Возвращает фокус окну, которое было активно до окошка (правило 0.6).
    public static Action? RestoreForeground { get; set; }

    protected Popup(bool activating)
    {
        _activating = activating;
        Win = new ShellWindow(new AlwaysActiveAcrylic(), noActivate: !activating, rounded: true);
        _host = new ContentControl
        {
            IsTabStop = activating,
            UseSystemFocusVisuals = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            RenderTransform = _shift,
        };
        Win.Root.Children.Add(_host);
        Win.Root.KeyDown += OnKeyDown;
        Win.Window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) { _wasActive = true; return; }
            // Клик мимо: окошко закрывается, а клик срабатывает там, куда нажали (0.4).
            if (_activating && _wasActive && IsOpen) Close(restoreFocus: false);
        };
    }

    protected void SetContent(UIElement content) => _host.Content = content;

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleKey(e.Key)) { e.Handled = true; return; }
        if (e.Key == VirtualKey.Escape) { e.Handled = true; Close(restoreFocus: true); }
    }

    /// Клавиши окошка (стрелки в меню). true — обработано.
    protected virtual bool HandleKey(VirtualKey key) => false;

    /// Размер содержимого в DIP при заданной ширине.
    protected virtual Size Measure(double width)
    {
        var el = (UIElement)_host.Content;
        el.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
        return el.DesiredSize;
    }

    /// Открыть над прямоугольником кнопки (физические пиксели экрана).
    /// <param name="beside">Справа от кнопки (подменю), а не над ней.</param>
    /// <param name="nested">Подменю: не считается отдельным окошком и не закрывает родителя.</param>
    public void Open(object anchor, Rect anchorPx, bool beside = false, bool nested = false)
    {
        if (!nested)
        {
            if (Popups.Current != null && Popups.Current != this) Popups.Current.Close(restoreFocus: false);
            Popups.Current = this;
        }
        _beside = beside;
        Anchor = anchor;
        _anchorPx = anchorPx;
        _closing = false;
        _wasActive = false;
        Layout();
        Animate();
        Win.Show(_activating);
        if (_activating) _host.Focus(FocusState.Programmatic);
        OnOpened();
    }

    protected virtual void OnOpened() { }

    /// Пересчитать размер и место (после смены содержимого).
    protected void Layout()
    {
        var mon = MonitorFromPoint(new POINT { X = (int)(_anchorPx.X + _anchorPx.Width / 2), Y = (int)_anchorPx.Y }, MONITOR_DEFAULTTONEAREST);
        var scale = MonitorScale(mon);
        var work = MonitorInfo(mon).rcWork;
        var size = Measure(Width);
        var w = (int)Math.Ceiling((Width > 0 ? Width : size.Width) * scale);
        var h = (int)Math.Ceiling(size.Height * scale);
        var margin = (int)(8 * scale);
        int x, y;
        if (_beside)
        {
            // Подменю: справа от пункта, верх по пункту; не влезает — слева.
            x = (int)(_anchorPx.X + _anchorPx.Width + 2 * scale);
            if (x + w > work.Right - margin) x = (int)(_anchorPx.X - 2 * scale - w);
            y = (int)(_anchorPx.Y - 4 * scale);
            y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - margin - h));
        }
        else
        {
            x = (int)(_anchorPx.X + _anchorPx.Width / 2 - w / 2.0);
            y = (int)(_anchorPx.Y - Gap * scale - h);
            x = Math.Clamp(x, work.Left + margin, Math.Max(work.Left + margin, work.Right - margin - w));
            if (y < work.Top + margin) y = work.Top + margin;
        }
        Win.Place(x, y, w, h);
    }

    /// Появление 150 мс: лёгкий выезд на 8 px и проявление (правило 0.8).
    void Animate()
    {
        if (!AnimationsEnabled()) { _host.Opacity = 1; _shift.TranslateY = 0; return; }
        var sb = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = Theme.Ms(150), EasingFunction = ease };
        Storyboard.SetTarget(fade, _host);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var move = new DoubleAnimation { From = 8, To = 0, Duration = Theme.Ms(150), EasingFunction = ease };
        Storyboard.SetTarget(move, _shift);
        Storyboard.SetTargetProperty(move, "TranslateY");
        sb.Children.Add(fade);
        sb.Children.Add(move);
        sb.Begin();
    }

    public void Close(bool restoreFocus)
    {
        if (!Win.Visible || _closing) return;
        _closing = true;
        var anchor = Anchor;
        OnClosing();
        // Фокус возвращаем, пока окошко ещё наше и активно: потом Windows может не разрешить.
        if (restoreFocus && _activating) RestoreForeground?.Invoke();
        Win.Hide();
        _closing = false;
        if (Popups.Current == this) Popups.Current = null;
        Popups.NoteClosed(anchor);
        Anchor = null;
        Closed?.Invoke();
    }

    protected virtual void OnClosing() { }

    /// Окошко под курсором (для списков, которые закрываются, когда курсор ушёл).
    public bool ContainsCursor()
    {
        if (!Win.Visible || !GetWindowRect(Win.Hwnd, out var r) || !GetCursorPos(out var p)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
}
