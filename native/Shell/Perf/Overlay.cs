using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using WindowsShell.Config;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Perf;

/// Игровой оверлей (design-v2 п. 8а): цифры в углу монитора с игрой. Своё окно поверх игры,
/// в игру не встраиваемся. Окно «слоистое» и прозрачное для мыши: клики уходят в игру, фокус
/// не берёт. Рисуется GDI+ два раза в секунду.
internal sealed class Overlay : IDisposable
{
    const string ClassName = "WindowsShellNative.PerfOverlay";
    static WndProc? _proc;
    static bool _registered;

    public static readonly Color Warn = Color.FromArgb(0xFF, 0xC0, 0x4D), Hot = Color.FromArgb(0xFF, 0x5B, 0x5B);
    static readonly Color Label = Color.FromArgb(0xB4, 0xB8, 0xBF), Value = Color.White;

    IntPtr _hwnd;
    bool _shown;
    long _raisedAt;

    public bool Visible => _shown;
    public IntPtr Hwnd => _hwnd;

    /// Нарисовать и поставить в угол монитора `screen` (физические пиксели), показать.
    public void Show(PerfSettings s, PerfNow now, RECT screen, double scale)
    {
        using var bmp = Render(s, now, scale);
        if (bmp is null) { Hide(); return; }
        Create();
        var margin = (int)Math.Round(12 * scale);
        var x = s.Corner is PerfSettings.TopRight or PerfSettings.BottomRight ? screen.Right - margin - bmp.Width : screen.Left + margin;
        var y = s.Corner is PerfSettings.BottomLeft or PerfSettings.BottomRight ? screen.Bottom - margin - bmp.Height : screen.Top + margin;
        Push(bmp, x, y);
        if (!_shown)
        {
            ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
            _shown = true;
        }
        // Игра тоже «поверх всех»: раз в секунду поднимаемся над ней.
        if (Environment.TickCount64 - _raisedAt > 1000)
        {
            _raisedAt = Environment.TickCount64;
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    public void Hide()
    {
        if (!_shown) return;
        ShowWindow(_hwnd, 0 /* SW_HIDE */);
        _shown = false;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _shown = false;
    }

    // ---------- что рисуем ----------

    enum Kind { Label, Value, Unit }
    sealed record Run(string Text, Kind Kind, Color Color, string? Template = null);

    /// Группы по порядку настроек: соседние FPS и время кадра, процессор % и °C и т. д. — одна группа.
    static List<(string Label, List<Run> Runs)> Groups(PerfSettings s, PerfNow now)
    {
        var groups = new List<(string Group, string Label, List<Run> Runs)>();
        foreach (var id in s.Items)
        {
            var (group, label) = id switch
            {
                "fps" or "frametime" => ("fps", "FPS"),
                "cpu" or "cputemp" => ("cpu", "ЦП"),
                "gpu" or "gputemp" => ("gpu", "ГП"),
                "ram" => ("ram", "ОЗУ"),
                _ => ("vram", "ВП"),
            };
            if (groups.Count == 0 || groups[^1].Group != group) groups.Add((group, label, []));
            var runs = groups[^1].Runs;
            var smp = now.Sample;
            switch (id)
            {
                case "fps":
                    runs.Add(new(now.Fps is { } f ? $"{f:0}" : "—", Kind.Value, now.Fps is { } ff && s.FpsWarn > 0 && ff < s.FpsWarn ? Warn : Value, "000"));
                    break;
                case "frametime":
                    runs.Add(new(now.Fps is { } f2 && f2 > 0 ? $"{1000 / f2:0.0}" : "—", Kind.Value, Value, "00.0"));
                    runs.Add(new("мс", Kind.Unit, Label));
                    break;
                case "cpu":
                    runs.Add(new(smp is null ? "—" : $"{smp.Cpu:0}%", Kind.Value, Value, "00%"));
                    break;
                case "gpu":
                    runs.Add(new(smp?.Gpu is { } g ? $"{g:0}%" : "—", Kind.Value, Value, "00%"));
                    break;
                case "cputemp":
                    runs.Add(Temp(smp?.CpuTemp));
                    break;
                case "gputemp":
                    runs.Add(Temp(smp?.GpuTemp));
                    break;
                case "ram":
                    runs.Add(new(smp is null ? "—" : Gb(smp.RamUsed), Kind.Value, Value, "00.0"));
                    if (smp != null) runs.Add(new($"/{Gb(smp.RamTotal, whole: true)} ГБ", Kind.Unit, Label));
                    break;
                case "vram":
                    runs.Add(new(smp?.VramUsed is { } v ? Gb(v) : "—", Kind.Value, Value, "00.0"));
                    if (smp?.VramTotal is { } vt) runs.Add(new($"/{Gb(vt, whole: true)} ГБ", Kind.Unit, Label));
                    break;
            }
        }
        return groups.Select(g => (g.Label, g.Runs)).ToList();
    }

    static Run Temp(double? t) => new(t is { } v ? $"{v:0}°" : "—", Kind.Value, TempColor(t) ?? Value, "00°");

    /// ≥ 80 °C жёлтый, ≥ 90 °C красный (design-v2 п. 8а).
    public static Color? TempColor(double? t) => t switch { >= 90 => Hot, >= 80 => Warn, _ => null };

    public static string Gb(double bytes, bool whole = false)
    {
        var gb = bytes / (1024.0 * 1024 * 1024);
        return whole ? $"{Math.Round(gb):0}" : gb.ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
    }

    /// Картинка оверлея (null — показывать нечего). Нужна и проверке (--perf-test).
    public static Bitmap? Render(PerfSettings s, PerfNow now, double scale)
    {
        var groups = Groups(s, now);
        if (groups.Count == 0) return null;
        var size = (float)(s.FontSize * scale);
        using var family = new FontFamily("Segoe UI");
        using var valueFont = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelFont = new Font(family, size * 0.72f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var probe = new Bitmap(1, 1);
        using var pg = Graphics.FromImage(probe);
        pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var fmt = StringFormat.GenericTypographic;
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        float W(string text, Font f) => text.Length == 0 ? 0 : pg.MeasureString(text, f, int.MaxValue, fmt).Width;
        Font FontOf(Kind k) => k == Kind.Value ? valueFont : labelFont;

        var gapRun = size * 0.28f;
        var gapLabel = size * 0.38f;
        var gapGroup = size * 0.95f;
        var padX = size * 0.6f;
        var padY = size * 0.32f;
        var lineH = size * 1.32f;
        // Ширина метки — по самой длинной, чтобы столбик был ровным.
        var labelW = s.Column ? groups.Max(g => W(g.Label, labelFont)) : 0;

        // Раскладка: каждая группа — список (x, run, ширина).
        var lines = new List<List<(float X, Run Run, float Width)>>();
        var widths = new List<float>();
        var cur = new List<(float, Run, float)>();
        float x = 0;
        foreach (var (label, runs) in groups)
        {
            if (s.Column && cur.Count > 0) { lines.Add(cur); widths.Add(x); cur = []; x = 0; }
            else if (cur.Count > 0) x += gapGroup;
            var lw = s.Column ? labelW : W(label, labelFont);
            cur.Add((x, new Run(label, Kind.Label, Label), lw));
            x += lw + gapLabel;
            // Внутри группы всё вплотную; запас под длинные числа (99 → 100) — в конце группы,
            // чтобы следующие группы не дёргались.
            float reserve = 0;
            for (var i = 0; i < runs.Count; i++)
            {
                var r = runs[i];
                if (i > 0) x += r.Kind == Kind.Unit ? gapRun * 0.6f : gapRun * 1.6f;
                var w = W(r.Text, FontOf(r.Kind));
                if (r.Template is { } t) reserve += Math.Max(0, W(t, FontOf(r.Kind)) - w);
                cur.Add((x, r, w));
                x += w;
            }
            x += reserve;
        }
        lines.Add(cur);
        widths.Add(x);

        var width = (int)Math.Ceiling(widths.Max() + padX * 2);
        var height = (int)Math.Ceiling(lines.Count * lineH + padY * 2);
        var bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var alpha = (int)Math.Round(255 * s.Background / 100.0);
        if (alpha > 0)
        {
            using var bg = new SolidBrush(Color.FromArgb(alpha, 10, 12, 14));
            using var path = Rounded(new RectangleF(0, 0, width, height), (float)(7 * scale));
            g.FillPath(bg, path);
        }
        var valueAscent = Ascent(valueFont);
        var labelAscent = Ascent(labelFont);
        for (var li = 0; li < lines.Count; li++)
        {
            var baseline = padY + li * lineH + (lineH + valueAscent * 0.78f) / 2;
            foreach (var (rx, run, _) in lines[li])
            {
                var font = FontOf(run.Kind);
                // Всё прижато влево; запас ширины значения — справа от него.
                const float dx = 0;
                var y = baseline - (run.Kind == Kind.Value ? valueAscent : labelAscent);
                if (alpha < 100)
                {
                    using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
                    g.DrawString(run.Text, font, shadow, padX + rx + dx + 1, y + 1, fmt);
                }
                using var brush = new SolidBrush(run.Color);
                g.DrawString(run.Text, font, brush, padX + rx + dx, y, fmt);
            }
        }
        return bmp;
    }

    static float Ascent(Font f) => f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- окно ----------

    void Create()
    {
        if (_hwnd != IntPtr.Zero) return;
        if (!_registered)
        {
            _proc = DefWindowProcW;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandleW(null),
                lpszClassName = ClassName,
            };
            RegisterClassExW(ref wc);
            _registered = true;
        }
        _hwnd = CreateWindowExW((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */,
            ClassName, "Монитор ПК", 0x80000000 /* WS_POPUP */, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    void Push(Bitmap bmp, int x, int y)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        try
        {
            var dst = new POINT { X = x, Y = y };
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
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

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE size, IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
