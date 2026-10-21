using System.Runtime.InteropServices;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Shots;

/// Что выбрали: прямоугольник экрана (физические пиксели), окно целиком или весь монитор.
public sealed record Selection(RECT Rect, IntPtr Window, bool Monitor);

/// Выбор области поверх замёрзшего экрана (design-v2 п. 4): одно окно на все мониторы, в нём
/// затемнённый снимок экрана. Тянешь рамку — область; клик — окно под курсором (оно подсвечено);
/// Пробел — весь монитор под курсором; Esc или правый клик — отмена. Лупа с перекрестием у
/// курсора и размер рамки в пикселях; стрелки двигают курсор на 1 px (с Shift — на 10).
///
/// Обычное окно Win32 с рисованием GDI, а не WinUI: появляется сразу, рисует пиксель в пиксель
/// и не зависит от масштаба монитора.
internal sealed class Selector
{
    const string ClassName = "WindowsShellNative.Snip";
    const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONUP = 0x0205, WM_KEYDOWN = 0x0100, WM_ACTIVATE = 0x0006, WM_SETCURSOR = 0x0020, WM_CAPTURECHANGED = 0x0215;
    const int VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_RETURN = 0x0D, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    const int Zoom = 8, Sample = 15;

    static readonly int Accent = Gdi.Rgb(0x4C, 0xC2, 0xFF);
    static readonly int Panel = Gdi.Rgb(0x2B, 0x2D, 0x31);
    static readonly int TextColor = Gdi.Rgb(0xF3, 0xF4, 0xF6);
    static readonly int TextDim = Gdi.Rgb(0xA9, 0xAD, 0xB4);

    static WndProc? _proc;
    static bool _registered;
    static Selector? _current;

    readonly Frozen _f;
    readonly bool _record;
    readonly IReadOnlyList<Screen> _screens;
    readonly List<(RECT Rect, IntPtr Hwnd)> _windows;
    readonly TaskCompletionSource<Selection?> _done = new();
    readonly Dictionary<int, IntPtr> _fonts = [];
    readonly IntPtr _brightDc, _dimDc, _oldBright, _oldDim;
    IntPtr _hwnd;
    POINT _cursor, _start;
    bool _dragging, _wasActive, _finished;
    List<RECT> _drawn = [];

    Selector(Frozen f, IReadOnlyList<Screen> screens, bool record)
    {
        _f = f;
        _record = record;
        _screens = screens;
        _windows = Windows(f.Bounds);
        var screen = Gdi.GetDC(IntPtr.Zero);
        _brightDc = Gdi.CreateCompatibleDC(screen);
        _dimDc = Gdi.CreateCompatibleDC(screen);
        Gdi.ReleaseDC(IntPtr.Zero, screen);
        _oldBright = Gdi.SelectObject(_brightDc, f.Bright);
        _oldDim = Gdi.SelectObject(_dimDc, f.Dim);
    }

    /// Показать выбор и дождаться ответа (null — отмена). Зовётся в потоке интерфейса.
    public static Task<Selection?> Pick(Frozen f, IReadOnlyList<Screen> screens, bool record)
    {
        _current?.Finish(null);
        var s = new Selector(f, screens, record);
        _current = s;
        s.Open();
        return s._done.Task;
    }

    /// Выбор открыт (второе PrtSc, пока выбираешь, ничего не делает).
    public static bool IsOpen => _current != null;

    void Open()
    {
        Register();
        _hwnd = CreateWindowExW((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW), ClassName, "Снимок", 0x80000000 /* WS_POPUP */,
            _f.X, _f.Y, _f.Width, _f.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) { Log.Write($"снимок: окно выбора не создалось ({Marshal.GetLastWin32Error()})"); Finish(null); return; }
        Cover = _hwnd;
        GetCursorPos(out _cursor);
        _drawn = Elements();
        ShowWindow(_hwnd, 5 /* SW_SHOW */);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        Ui.ShellWindow.ForceForeground(_hwnd);
        SetCursor(LoadCursorW(IntPtr.Zero, 32515 /* IDC_CROSS */));
    }

    static void Register()
    {
        if (_registered) return;
        _proc = Proc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(IntPtr.Zero, 32515),
            lpszClassName = ClassName,
        };
        RegisterClassExW(ref wc);
        _registered = true;
    }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        var s = _current;
        try
        {
            if (s != null && s._hwnd == h && s.Handle(msg, w, out var result)) return result;
        }
        catch (Exception e)
        {
            Log.Error("снимок: выбор области", e);
            s?.Finish(null);
        }
        return DefWindowProcW(h, msg, w, l);
    }

    bool Handle(uint msg, IntPtr w, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case WM_PAINT:
                Paint();
                return true;
            case WM_ERASEBKGND:
                result = 1;
                return true;
            case WM_SETCURSOR:
                SetCursor(LoadCursorW(IntPtr.Zero, 32515));
                result = 1;
                return true;
            case WM_MOUSEMOVE:
                GetCursorPos(out _cursor);
                Update();
                return true;
            case WM_LBUTTONDOWN:
                GetCursorPos(out _cursor);
                _start = _cursor;
                _dragging = true;
                SetCapture(_hwnd);
                Update();
                return true;
            case WM_LBUTTONUP:
                if (!_dragging) return true;
                GetCursorPos(out _cursor);
                _dragging = false;
                ReleaseCapture();
                // Почти не сдвинулся — это клик: окно (или монитор, если под курсором рабочий стол).
                if (Math.Abs(_cursor.X - _start.X) < 4 && Math.Abs(_cursor.Y - _start.Y) < 4) FinishHover();
                else Finish(new Selection(DragRect(), IntPtr.Zero, false));
                return true;
            case WM_CAPTURECHANGED:
                // Мышь забрали посреди рамки (всплыло чужое окно) — рамку бросаем, выбор остаётся.
                if (_dragging) { _dragging = false; Update(); }
                return true;
            case WM_RBUTTONUP:
                Finish(null);
                return true;
            case WM_ACTIVATE:
                if (((int)w & 0xFFFF) != 0) _wasActive = true;
                // Ушли в другое окно (Alt+Tab) — отмена.
                else if (_wasActive) Finish(null);
                return true;
            case WM_KEYDOWN:
                Key((int)w);
                return true;
        }
        return false;
    }

    void Key(int vk)
    {
        var step = KeyDown(VK_SHIFT) ? 10 : 1;
        switch (vk)
        {
            case VK_ESCAPE: Finish(null); break;
            case VK_SPACE:
                var m = ScreenAt(_cursor);
                Finish(new Selection(m.Bounds, IntPtr.Zero, true));
                break;
            case VK_RETURN when !_dragging: FinishHover(); break;
            case VK_LEFT: Nudge(-step, 0); break;
            case VK_RIGHT: Nudge(step, 0); break;
            case VK_UP: Nudge(0, -step); break;
            case VK_DOWN: Nudge(0, step); break;
        }
    }

    void Nudge(int dx, int dy)
    {
        GetCursorPos(out var p);
        SetCursorPos(p.X + dx, p.Y + dy);
    }

    void FinishHover()
    {
        var (rect, hwnd) = Hover();
        Finish(new Selection(rect, hwnd, hwnd == IntPtr.Zero));
    }

    void Finish(Selection? result)
    {
        if (_finished) return;
        _finished = true;
        if (_current == this) _current = null;
        if (Cover == _hwnd) Cover = IntPtr.Zero;
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        Gdi.SelectObject(_brightDc, _oldBright);
        Gdi.SelectObject(_dimDc, _oldDim);
        Gdi.DeleteDC(_brightDc);
        Gdi.DeleteDC(_dimDc);
        foreach (var f in _fonts.Values) Gdi.DeleteObject(f);
        if (result != null)
        {
            var r = Gdi.Intersect(result.Rect, _f.Bounds);
            result = Gdi.Empty(r) ? null : result with { Rect = r };
        }
        _done.TrySetResult(result);
    }

    // ---------- что где ----------

    RECT DragRect() => Gdi.Rect(Math.Min(_start.X, _cursor.X), Math.Min(_start.Y, _cursor.Y),
        Math.Max(_start.X, _cursor.X) + 1, Math.Max(_start.Y, _cursor.Y) + 1);

    Screen ScreenAt(POINT p) => _screens.FirstOrDefault(s => s.Contains(p.X, p.Y)) ?? _screens.First();

    /// Окно под курсором (по z-порядку на момент снимка); нет окна — монитор.
    (RECT Rect, IntPtr Hwnd) Hover()
    {
        foreach (var (r, h) in _windows)
            if (_cursor.X >= r.Left && _cursor.X < r.Right && _cursor.Y >= r.Top && _cursor.Y < r.Bottom) return (r, h);
        return (ScreenAt(_cursor).Bounds, IntPtr.Zero);
    }

    /// Рамка, которую сейчас видно: тянется или подсвеченное окно.
    RECT Current() => _dragging ? DragRect() : Hover().Rect;

    double Scale => ScreenAt(_cursor).Scale;

    int Px(double dip) => (int)Math.Round(dip * Scale);

    /// Где что нарисовано (в координатах экрана): рамка с подписью, лупа, подсказка.
    sealed record Layout(RECT Sel, RECT Label, string LabelText, RECT Loupe, RECT Hint, string HintText);

    Layout Arrange()
    {
        var sel = Current();
        var monitor = ScreenAt(_cursor).Bounds;
        var labelText = $"{sel.Width} × {sel.Height}";
        var font = Font(12);
        var (lw, lh) = Measure(labelText, font);
        lw += Px(16); lh += Px(6);
        var ly = sel.Top - lh - Px(6);
        var label = ly >= monitor.Top
            ? Gdi.Rect(sel.Left, ly, sel.Left + lw, ly + lh)
            : Gdi.Rect(sel.Left + Px(6), sel.Top + Px(6), sel.Left + Px(6) + lw, sel.Top + Px(6) + lh);

        var size = Sample * Px(Zoom);
        var lx = _cursor.X + Px(20);
        var lyy = _cursor.Y + Px(20);
        if (lx + size > monitor.Right) lx = _cursor.X - Px(20) - size;
        if (lyy + size > monitor.Bottom) lyy = _cursor.Y - Px(20) - size;
        var loupe = Gdi.Rect(lx, lyy, lx + size, lyy + size);

        var hintText = _record
            ? "Тяни — область записи     Клик — окно     Пробел — весь монитор     Esc — отмена"
            : "Тяни — область     Клик — окно     Пробел — весь монитор     Esc — отмена";
        var (hw, hh) = Measure(hintText, Font(13));
        hw += Px(32); hh += Px(18);
        var hx = (monitor.Left + monitor.Right - hw) / 2;
        var hy = monitor.Bottom - hh - Px(96);
        var hint = _dragging ? default : Gdi.Rect(hx, hy, hx + hw, hy + hh);
        return new Layout(sel, label, labelText, loupe, hint, hintText);
    }

    List<RECT> Elements()
    {
        var l = Arrange();
        var b = Px(3);
        var list = new List<RECT> { Gdi.Inflate(l.Sel, b), l.Label, Gdi.Inflate(l.Loupe, Px(3)) };
        if (!Gdi.Empty(l.Hint)) list.Add(l.Hint);
        return list;
    }

    void Update()
    {
        var now = Elements();
        foreach (var r in _drawn.Concat(now)) Invalidate(r);
        _drawn = now;
    }

    void Invalidate(RECT screen)
    {
        var r = Gdi.Rect(screen.Left - _f.X, screen.Top - _f.Y, screen.Right - _f.X, screen.Bottom - _f.Y);
        Gdi.InvalidateRect(_hwnd, ref r, false);
    }

    // ---------- рисование ----------

    void Paint()
    {
        var hdc = Gdi.BeginPaint(_hwnd, out var ps);
        try
        {
            var p = ps.rcPaint;
            if (Gdi.Empty(p)) return;
            var mem = Gdi.CreateCompatibleDC(hdc);
            var bmp = Gdi.CreateCompatibleBitmap(hdc, p.Width, p.Height);
            var old = Gdi.SelectObject(mem, bmp);
            Gdi.SetViewportOrgEx(mem, -p.Left, -p.Top, IntPtr.Zero);
            Gdi.BitBlt(mem, p.Left, p.Top, p.Width, p.Height, _dimDc, p.Left, p.Top, Gdi.SRCCOPY);
            Draw(mem, p);
            Gdi.SetViewportOrgEx(mem, 0, 0, IntPtr.Zero);
            Gdi.BitBlt(hdc, p.Left, p.Top, p.Width, p.Height, mem, 0, 0, Gdi.SRCCOPY);
            Gdi.SelectObject(mem, old);
            Gdi.DeleteObject(bmp);
            Gdi.DeleteDC(mem);
        }
        finally
        {
            Gdi.EndPaint(_hwnd, ref ps);
        }
    }

    /// Всё в координатах окна (экран минус левый верх виртуального экрана).
    void Draw(IntPtr dc, RECT paint)
    {
        var l = Arrange();
        RECT Local(RECT r) => Gdi.Rect(r.Left - _f.X, r.Top - _f.Y, r.Right - _f.X, r.Bottom - _f.Y);

        // Рамка: внутри как было, вокруг тонкая голубая линия.
        var sel = Local(l.Sel);
        var inside = Gdi.Intersect(sel, paint);
        if (!Gdi.Empty(inside)) Gdi.BitBlt(dc, inside.Left, inside.Top, inside.Width, inside.Height, _brightDc, inside.Left, inside.Top, Gdi.SRCCOPY);
        var t = Math.Max(2, Px(2));
        var accent = Gdi.CreateSolidBrush(Accent);
        Fill(dc, Gdi.Rect(sel.Left - t, sel.Top - t, sel.Right + t, sel.Top), accent);
        Fill(dc, Gdi.Rect(sel.Left - t, sel.Bottom, sel.Right + t, sel.Bottom + t), accent);
        Fill(dc, Gdi.Rect(sel.Left - t, sel.Top, sel.Left, sel.Bottom), accent);
        Fill(dc, Gdi.Rect(sel.Right, sel.Top, sel.Right + t, sel.Bottom), accent);
        if (_dragging)
        {
            // Уголки, как в ShareX и на картинке.
            var k = Px(4);
            var white = Gdi.CreateSolidBrush(Gdi.Rgb(255, 255, 255));
            foreach (var (x, y) in new[] { (sel.Left, sel.Top), (sel.Right, sel.Top), (sel.Left, sel.Bottom), (sel.Right, sel.Bottom) })
            {
                Fill(dc, Gdi.Rect(x - k - 1, y - k - 1, x + k + 1, y + k + 1), accent);
                Fill(dc, Gdi.Rect(x - k, y - k, x + k, y + k), white);
            }
            Gdi.DeleteObject(white);
        }
        Gdi.DeleteObject(accent);

        // Размер рамки.
        Bubble(dc, Local(l.Label), l.LabelText, Font(12), TextColor);

        // Лупа: 15×15 пикселей под курсором в 8 раз крупнее, по центру — пиксель курсора.
        var loupe = Local(l.Loupe);
        var zoom = loupe.Width / Sample;
        var region = Gdi.CreateEllipticRgn(loupe.Left - paint.Left, loupe.Top - paint.Top, loupe.Right - paint.Left + 1, loupe.Bottom - paint.Top + 1);
        Gdi.SelectClipRgn(dc, region);
        Gdi.SetStretchBltMode(dc, Gdi.COLORONCOLOR);
        Gdi.StretchBlt(dc, loupe.Left, loupe.Top, zoom * Sample, zoom * Sample, _brightDc,
            _cursor.X - _f.X - Sample / 2, _cursor.Y - _f.Y - Sample / 2, Sample, Sample, Gdi.SRCCOPY);
        var cx = loupe.Left + Sample / 2 * zoom;
        var cy = loupe.Top + Sample / 2 * zoom;
        var cross = Gdi.CreateSolidBrush(Accent);
        Fill(dc, Gdi.Rect(cx + zoom / 2 - 1, loupe.Top, cx + zoom / 2 + 1, cy), cross);
        Fill(dc, Gdi.Rect(cx + zoom / 2 - 1, cy + zoom, cx + zoom / 2 + 1, loupe.Bottom), cross);
        Fill(dc, Gdi.Rect(loupe.Left, cy + zoom / 2 - 1, cx, cy + zoom / 2 + 1), cross);
        Fill(dc, Gdi.Rect(cx + zoom, cy + zoom / 2 - 1, loupe.Right, cy + zoom / 2 + 1), cross);
        Gdi.DeleteObject(cross);
        Gdi.SelectClipRgn(dc, IntPtr.Zero);
        Gdi.DeleteObject(region);
        var pen = Gdi.CreatePen(Gdi.PS_SOLID, Math.Max(2, Px(2)), Gdi.Rgb(255, 255, 255));
        var oldPen = Gdi.SelectObject(dc, pen);
        var oldBrush = Gdi.SelectObject(dc, Gdi.GetStockObject(Gdi.NULL_BRUSH));
        Gdi.Ellipse(dc, loupe.Left, loupe.Top, loupe.Right, loupe.Bottom);
        // Квадратик вокруг пикселя под курсором.
        var box = Gdi.CreatePen(Gdi.PS_SOLID, 1, Gdi.Rgb(255, 255, 255));
        Gdi.SelectObject(dc, box);
        Gdi.Rectangle(dc, cx - 1, cy - 1, cx + zoom + 1, cy + zoom + 1);
        Gdi.SelectObject(dc, oldPen);
        Gdi.SelectObject(dc, oldBrush);
        Gdi.DeleteObject(pen);
        Gdi.DeleteObject(box);

        if (!Gdi.Empty(l.Hint)) Bubble(dc, Local(l.Hint), l.HintText, Font(13), TextDim);
    }

    static void Fill(IntPtr dc, RECT r, IntPtr brush)
    {
        if (!Gdi.Empty(r)) Gdi.FillRect(dc, ref r, brush);
    }

    /// Тёмная плашка со скруглёнными углами и текстом по центру.
    void Bubble(IntPtr dc, RECT r, string text, IntPtr font, int color)
    {
        var brush = Gdi.CreateSolidBrush(Panel);
        var pen = Gdi.CreatePen(Gdi.PS_SOLID, 1, Gdi.Rgb(0x45, 0x48, 0x4E));
        var oldBrush = Gdi.SelectObject(dc, brush);
        var oldPen = Gdi.SelectObject(dc, pen);
        Gdi.RoundRect(dc, r.Left, r.Top, r.Right, r.Bottom, Px(12), Px(12));
        Gdi.SelectObject(dc, oldBrush);
        Gdi.SelectObject(dc, oldPen);
        Gdi.DeleteObject(brush);
        Gdi.DeleteObject(pen);
        var oldFont = Gdi.SelectObject(dc, font);
        Gdi.SetBkMode(dc, Gdi.TRANSPARENT);
        Gdi.SetTextColor(dc, color);
        Gdi.DrawTextW(dc, text, text.Length, ref r, Gdi.DT_CENTER | Gdi.DT_VCENTER | Gdi.DT_SINGLELINE | Gdi.DT_NOPREFIX);
        Gdi.SelectObject(dc, oldFont);
    }

    IntPtr Font(int pt)
    {
        var px = Px(pt * 96 / 72.0);
        if (_fonts.TryGetValue(px, out var f)) return f;
        f = Gdi.CreateFontW(-px, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE */, 0, "Segoe UI");
        _fonts[px] = f;
        return f;
    }

    (int W, int H) Measure(string text, IntPtr font)
    {
        var dc = Gdi.GetDC(IntPtr.Zero);
        var old = Gdi.SelectObject(dc, font);
        var r = new RECT();
        Gdi.DrawTextW(dc, text, text.Length, ref r, 0x400 /* DT_CALCRECT */ | Gdi.DT_SINGLELINE | Gdi.DT_NOPREFIX);
        Gdi.SelectObject(dc, old);
        Gdi.ReleaseDC(IntPtr.Zero, dc);
        return (r.Width, r.Height);
    }

    // ---------- окна на экране ----------

    /// Видимые окна сверху вниз, без наших, без рабочего стола и с рамкой без тени.
    static List<(RECT, IntPtr)> Windows(RECT screen)
    {
        var list = new List<(RECT, IntPtr)>();
        var own = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || IsCloaked(h) || ProcessId(h) == own) return true;
            var cls = Win32.ClassName(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            var ex = ExStyle(h);
            // Прозрачные для мыши слои поверх экрана (оверлеи игр, видеокарты) не выбираются.
            if ((ex & 0x20) != 0 && (ex & 0x80000) != 0) return true;
            if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r)) return true;
            r = Gdi.Intersect(r, screen);
            if (r.Width < 8 || r.Height < 8) return true;
            list.Add((r, h));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr inst, int id);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
