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
        _watch.Interval = TimeSpan.FromMilliseconds(60);
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
        _fgBeforeOpen = GetForegroundWindow();
        _host.Prune();
        _clickedPid = 0;
        Build();
        Open(anchor, anchorPx, owner: owner);
        _watch.Start();
        HookMouse();
    }

    protected override void OnClosing()
    {
        _watch.Stop();
        UnhookMouse();
        _clickedAt = 0;
        _hadMenu = false;
        _keepFor = IntPtr.Zero;
        _placed.Clear();
        if (_menuOpen) { _menuOpen = false; RestoreOwn(); }
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
            if (it.Callback == 0) return;
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
            if (it.Callback == 0) { NoCallback(it, button); return; }
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

    MenuPopup? _ownMenu;

    /// Иконка, у которой мы не знаем сообщения для кликов: программа сообщила его Проводнику
    /// ещё до запуска оболочки и нам не повторила (так делает щит «Безопасность Windows»).
    /// Для известных программ делаем то же, что их меню; для остальных пишем в журнал.
    void NoCallback(TrayItem it, TrayHost.Button button)
    {
        var exe = ProcessName(ProcessId(it.Hwnd));
        Log.Write($"трей: у иконки {exe} нет сообщения для кликов");
        if (!exe.Equals("SecurityHealthSystray", StringComparison.OrdinalIgnoreCase)) return;
        void Open() { Close(restoreFocus: false); Apps.Launcher.Open("windowsdefender:"); }
        if (button == TrayHost.Button.Left) { Open(); return; }
        GetCursorPos(out var c);
        _ownMenu ??= new MenuPopup();
        _ownMenu.ShowInside(this, it, new Rect(c.X, c.Y, 0, 0),
        [
            new MenuItem { Text = "Открыть «Безопасность Windows»", Glyph = "\uE83D", Invoke = Open },
            new MenuItem { Text = "Защита от вирусов и угроз", Glyph = "\uE8A7", Invoke = () => { Close(restoreFocus: false); Apps.Launcher.Open("windowsdefender://threat"); } },
        ]);
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

    bool _menuOpen;
    // После клика по иконке: было ли меню, какие окна программы уже были открыты, и окно,
    // вышедшее вперёд после меню, которое окошко не закрывает.
    bool _hadMenu;
    readonly HashSet<IntPtr> _visibleAtClick = [];
    IntPtr _keepFor;
    IntPtr _fgBeforeOpen;

    // ---------- клик мимо ----------
    // Пока окошко открыто, слушаем нажатия мыши во всей системе (как окошко трея Windows): клик
    // по чужому окну, не по меню программы, закрывает окошко, даже если фокус не сменился (после
    // меню Chrome фокус уже у того окна, по которому кликают). В самом перехватчике ничего не
    // делаем — только ставим проверку в очередь окна.

    delegate IntPtr MouseProc(int code, IntPtr w, IntPtr l);
    MouseProc? _mouseProc;
    IntPtr _mouseHook;

    void HookMouse()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc ??= OnMouse;
        _mouseHook = SetWindowsHookExW(14 /* WH_MOUSE_LL */, _mouseProc, GetModuleHandleW(null), 0);
    }

    void UnhookMouse()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    IntPtr OnMouse(int code, IntPtr w, IntPtr l)
    {
        var msg = (uint)(long)w;
        if (code >= 0 && msg is 0x201 or 0x204 or 0x207 /* L, R, M down */)
        {
            var pt = System.Runtime.InteropServices.Marshal.PtrToStructure<POINT>(l);
            Win.Window.DispatcherQueue.TryEnqueue(() => ClickAt(pt));
        }
        return CallNextHookEx(IntPtr.Zero, code, w, l);
    }

    void ClickAt(POINT pt)
    {
        if (!IsOpen) return;
        var root = GetAncestor(WindowFromPoint(pt), 2 /* GA_ROOT */);
        if (root == IntPtr.Zero || ProcessId(root) == (uint)Environment.ProcessId || ClassName(root) == "#32768") return;
        if (FindPopups().Contains(root)) return;
        Close(restoreFocus: false);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetWindowsHookExW(int id, MouseProc proc, IntPtr mod, uint thread);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    readonly HashSet<IntPtr> _placed = [];

    /// Окна «поверх всех», которые были до клика (у Проводника их много, и это не меню).
    readonly HashSet<IntPtr> _before = [];

    static IEnumerable<IntPtr> TopmostWindows()
    {
        for (var h = GetWindow(GetDesktopWindow(), 5 /* GW_CHILD */); h != IntPtr.Zero; h = GetWindow(h, 2 /* GW_HWNDNEXT */))
        {
            if ((ExStyle(h) & WS_EX_TOPMOST) == 0) yield break;
            if (IsWindowVisible(h)) yield return h;
        }
    }

    /// Окна, которые программа вывела после клика по иконке: обычное меню Windows (#32768) или
    /// свои окна «поверх всех» (Chrome, Discord, Fan Control, Xbox, G HUB рисуют меню сами, вместе
    /// с подсказками, иногда в другом процессе). Наши окна не в счёт.
    List<IntPtr> FindPopups()
    {
        var list = new List<IntPtr>();
        var menu = FindWindowW("#32768", null);
        if (menu != IntPtr.Zero && IsWindowVisible(menu)) list.Add(menu);
        if (_clickedAt == 0) return list;
        var own = (uint)Environment.ProcessId;
        var late = Environment.TickCount64 - _clickedAt > 3000;
        foreach (var h in TopmostWindows())
        {
            if (h == menu || _before.Contains(h) || ProcessId(h) == own || IsRealWindow(h)) continue;
            if (ClassName(h) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") continue;
            // Чужое новое окно через много секунд после клика — уже не меню (уведомление и т. п.).
            if (late && ProcessId(h) != _clickedPid) continue;
            list.Add(h);
        }
        return list;
    }

    POINT _clickPoint;

    /// Программы ставят меню по месту панели задач Windows, а она у нас спрятана: Bluetooth —
    /// в угол экрана, Xbox — под нижний край, G HUB — в левый верхний угол. Меню, которое не на
    /// мониторе клика или далеко от иконки, переносим к иконке (над ней). Окна программ,
    /// запущенных от администратора, Windows двигать не даёт — их не трогаем.
    void PlaceStray(IntPtr menu)
    {
        if (!GetWindowRect(menu, out var r)) return;
        var mon = MonitorInfo(MonitorFromPoint(_clickPoint, MONITOR_DEFAULTTONEAREST)).rcMonitor;
        var onMonitor = r.Left >= mon.Left && r.Top >= mon.Top && r.Right <= mon.Right && r.Bottom <= mon.Bottom;
        var near = Math.Abs((r.Left + r.Right) / 2 - _clickPoint.X) < 700 && Math.Abs((r.Top + r.Bottom) / 2 - _clickPoint.Y) < 700;
        if (onMonitor && near) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var margin = 8;
        var x = Math.Clamp(_clickPoint.X - w / 2, mon.Left + margin, Math.Max(mon.Left + margin, mon.Right - margin - w));
        var y = Math.Clamp(_clickPoint.Y - h - margin, mon.Top + margin, Math.Max(mon.Top + margin, mon.Bottom - margin - h));
        var ok = SetWindowPos(menu, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (_placed.Add(menu)) Log.Write($"трей: меню {ClassName(menu)} перенесено из {r.Left},{r.Top} к иконке{(ok ? "" : " — не дали")}");
    }

    IntPtr _clickedHwnd;

    void NoteClick(TrayItem it)
    {
        _clickedHwnd = it.Hwnd;
        _before.Clear();
        foreach (var h in TopmostWindows()) _before.Add(h);
        GetCursorPos(out _clickPoint);
        _clickedPid = ProcessId(it.Hwnd);
        _hadMenu = false;
        _keepFor = IntPtr.Zero;
        _visibleAtClick.Clear();
        var pid = _clickedPid;
        EnumWindows((h, _) => { if (IsWindowVisible(h) && ProcessId(h) == pid) _visibleAtClick.Add(h); return true; }, IntPtr.Zero);
        _clickedAt = Environment.TickCount64;
    }

    static string ProcessName(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return pid.ToString(); }
    }

    /// Окошко живёт как в Windows 11: стоит, пока открыто меню программы и после него, и
    /// закрывается, когда ушли в другое окно (клик мимо, программа открыла своё окно), по Esc или
    /// повторному клику по ⌃. Меню программы на это не влияет — только на порядок окон: пока оно
    /// открыто, наши окна стоят под ним.
    void Watch()
    {
        if (!IsOpen) { _watch.Stop(); return; }
        var popups = FindPopups();
        if (popups.Count > 0)
        {
            if (!_menuOpen)
            {
                _menuOpen = true;
                Log.Write($"трей: меню {string.Join(", ", popups.Select(h => $"{ClassName(h)} ({ProcessName(ProcessId(h))})"))}");
            }
            _hadMenu = true;
            // Переносим только меню нажатой программы (и меню Windows): уведомление другой
            // программы, всплывшее в это время (Telegram), остаётся на месте.
            foreach (var h in popups)
                if (ProcessId(h) == _clickedPid || ClassName(h) == "#32768") PlaceStray(h);
            // Каждый раз: наше окно могло подняться между проверками (смена активного окна).
            var moved = DemoteOwn();
            if (moved > 0) Log.Write($"трей: наши окна под меню программы ({moved})");
        }
        else if (_menuOpen)
        {
            // Меню закрылось (Esc, выбрали пункт): наши окна снова поверх всех.
            _menuOpen = false;
            RestoreOwn();
            // Окну программы — пустое сообщение после меню (так советует Microsoft для меню из
            // трея): иначе его меню держит мышь и следующий клик по ⌃ пропадает.
            if (_clickedHwnd != IntPtr.Zero) PostMessageW(_clickedHwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            // Меню Chrome и Discord, закрываясь, отдают фокус окну, которое было впереди до ⌃.
            // Это не уход из окошка: фокус забираем назад (как окошко трея Windows), а если
            // Windows не даст — то окно окошко не закрывает.
            _keepFor = _fgBeforeOpen;
            SetForegroundWindow(Hwnd);
        }
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Hwnd || ProcessId(fg) == (uint)Environment.ProcessId) return;
        if (!IsRealWindow(fg) || popups.Contains(fg) || fg == _keepFor) return;
        // Меню закрыли (Esc), а программа вывела вперёд своё окно, которое и так было открыто
        // (Discord, Chrome, Claude, Xbox так делают): это не уход из окошка, оно остаётся.
        if (_hadMenu && ProcessId(fg) == _clickedPid && _visibleAtClick.Contains(fg)) { _keepFor = fg; return; }
        // Впереди обычное окно (рабочий стол, другая программа, окно, открытое кликом по иконке
        // или пунктом меню): окошко закрывается.
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr GetDesktopWindow();
}
