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

    /// Открыто ли окошко, которое открыла эта полоса (док, панель): полоса не уезжает.
    public static bool OwnedBy(object owner) => Current is { IsOpen: true } p && ReferenceEquals(p.Owner, owner);

    /// Открытое окошко сменилось (открылось или закрылось).
    public static event Action? CurrentChanged;

    internal static void RaiseChanged() => CurrentChanged?.Invoke();
}

public enum Placement { Above, Below, Beside, AtPoint }

/// Всплывающее окошко с настоящим стеклом (меню, окошко звука, список окон). Стоит над кнопкой,
/// выровнено по её центру и не выходит за край экрана.
public abstract class Popup
{
    protected readonly ShellWindow Win;
    readonly bool _activating;
    readonly ContentControl _host;
    int _openCount;
    readonly CompositeTransform _shift = new();
    Rect _anchorPx;
    Placement _placement;
    bool _closing;
    bool _wasActive;

    /// Кто открыл окошко (иконка дока, кнопка звука).
    public object? Anchor { get; private set; }

    /// Полоса, из которой открыто окошко.
    public object? Owner { get; private set; }
    public bool IsOpen => Win.Visible && !_closing;

    /// Окошко, внутри которого открыто это (меню плитки внутри окна категории). Пока оно
    /// открыто, хозяин не закрывается; закрылось — фокус возвращается хозяину.
    public Popup? Host { get; private set; }
    Popup? _child;
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
        Win = new ShellWindow(Surface.Menu, noActivate: !activating, rounded: true);
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
            // Внутреннее меню ещё открывается (его окно только что стало активным) или открыто.
            if (_child != null) return;
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

    /// Открыть у прямоугольника кнопки (физические пиксели экрана): над ней (док), под ней
    /// (панель) или справа (подменю).
    /// <param name="nested">Подменю: не считается отдельным окошком и не закрывает родителя.</param>
    /// Открыть внутри другого окошка (меню плитки в окне категории).
    public void OpenInside(Popup host, object anchor, Rect anchorPx, Placement placement = Placement.Above)
    {
        host._child?.Close(restoreFocus: false);
        Host = host;
        host._child = this;
        Open(anchor, anchorPx, placement, nested: true, owner: host.Owner);
    }

    public void Open(object anchor, Rect anchorPx, Placement placement = Placement.Above, bool nested = false, object? owner = null)
    {
        if (!nested)
        {
            if (Popups.Current != null && Popups.Current != this) Popups.Current.Close(restoreFocus: false);
            Popups.Current = this;
        }
        _placement = placement;
        Owner = owner;
        Anchor = anchor;
        _anchorPx = anchorPx;
        _closing = false;
        _wasActive = false;
        Layout();
        Animate();
        Win.Show(_activating);
        if (_activating) _host.Focus(FocusState.Programmatic);
        // Шаблоны полей ввода и переключателей применяются только в показанном окне, до этого
        // содержимое меряется ниже настоящего (поле названия обрезалось). Меряем ещё раз.
        var opened = ++_openCount;
        Win.Window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (IsOpen && opened == _openCount) Layout(); });
        OnOpened();
        if (!nested) Popups.RaiseChanged();
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
        if (_placement == Placement.Beside)
        {
            // Подменю: справа от пункта, верх по пункту; не влезает — слева.
            x = (int)(_anchorPx.X + _anchorPx.Width + 2 * scale);
            if (x + w > work.Right - margin) x = (int)(_anchorPx.X - 2 * scale - w);
            y = (int)(_anchorPx.Y - 4 * scale);
            y = Math.Clamp(y, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - margin - h));
        }
        else if (_placement == Placement.AtPoint)
        {
            // Контекстное меню у курсора: вправо-вниз, не влезает — в другую сторону.
            var area = MonitorInfo(mon).rcMonitor;
            x = (int)_anchorPx.X;
            y = (int)_anchorPx.Y;
            if (x + w > area.Right - margin) x -= w;
            if (y + h > area.Bottom - margin) y -= h;
            x = Math.Max(area.Left + margin, x);
            y = Math.Max(area.Top + margin, y);
        }
        else
        {
            x = (int)(_anchorPx.X + _anchorPx.Width / 2 - w / 2.0);
            // Окошко на мониторе своей кнопки и не выходит за его край (раздел 9). Рабочую
            // область не берём: закреплённый док сам её уменьшает, а окошко стоит над ним.
            var area = MonitorInfo(mon).rcMonitor;
            x = Math.Clamp(x, area.Left + margin, Math.Max(area.Left + margin, area.Right - margin - w));
            if (_placement == Placement.Below)
            {
                y = (int)(_anchorPx.Y + _anchorPx.Height + Gap * scale);
                if (y + h > area.Bottom - margin) y = Math.Max(area.Top + margin, area.Bottom - margin - h);
            }
            else
            {
                y = (int)(_anchorPx.Y - Gap * scale - h);
                if (y < area.Top + margin) y = area.Top + margin;
            }
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
        _child?.Close(restoreFocus: false);
        OnClosing();
        var host = Host;
        Host = null;
        if (host != null && host._child == this) host._child = null;
        if (host is { IsOpen: true })
        {
            // Внутреннее меню: фокус обратно хозяину; ушли в чужое окно — закрывается и хозяин.
            if (restoreFocus) SetForegroundWindow(host.Hwnd);
            else if (GetForegroundWindow() != host.Hwnd && ProcessId(GetForegroundWindow()) != (uint)Environment.ProcessId) host.Close(restoreFocus: false);
        }
        // Фокус возвращаем, пока окошко ещё наше и активно: потом Windows может не разрешить.
        else if (restoreFocus && _activating) RestoreForeground?.Invoke();
        Win.Hide();
        _closing = false;
        if (Popups.Current == this) Popups.Current = null;
        Popups.NoteClosed(anchor);
        Anchor = null;
        Owner = null;
        Closed?.Invoke();
        Popups.RaiseChanged();
    }

    protected virtual void OnClosing() { }

    /// Окошко под курсором (для списков, которые закрываются, когда курсор ушёл).
    public bool ContainsCursor()
    {
        if (!Win.Visible || !GetWindowRect(Win.Hwnd, out var r) || !GetCursorPos(out var p)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
}
