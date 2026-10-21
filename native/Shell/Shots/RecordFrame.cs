using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Shots;

/// Пока идёт запись (design-v2 п. 4): красная рамка вокруг области и маленькая панель у неё:
/// таймер · пауза · микрофон · звук системы · стоп. Ни рамка, ни панель в видео не попадают
/// (WDA_EXCLUDEFROMCAPTURE).
internal sealed class RecordFrame
{
    const string BorderClass = "WindowsShellNative.RecBorder";
    static WndProc? _proc;
    static bool _registered;

    readonly ShotsService _shots;
    readonly List<IntPtr> _edges = [];
    readonly ShellWindow _panel;
    readonly TextBlock _time = new() { FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text), VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
    readonly Border _dot = new() { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = Theme.Brush(Windows.UI.Color.FromArgb(255, 0xFF, 0x5B, 0x5B)), VerticalAlignment = VerticalAlignment.Center };
    readonly Button _pause, _mic, _sound;

    public RecordFrame(ShotsService shots, RECT region, Screen screen)
    {
        _shots = shots;
        // Рамка — снаружи области, на весь монитор не нужна.
        if (!(region.Left <= screen.Bounds.Left && region.Top <= screen.Bounds.Top && region.Right >= screen.Bounds.Right && region.Bottom >= screen.Bounds.Bottom))
        {
            var t = Math.Max(2, (int)Math.Round(2 * screen.Scale));
            Edge(region.Left - t, region.Top - t, region.Width + 2 * t, t);
            Edge(region.Left - t, region.Bottom, region.Width + 2 * t, t);
            Edge(region.Left - t, region.Top, t, region.Height);
            Edge(region.Right, region.Top, t, region.Height);
        }

        _panel = new ShellWindow(Surface.Menu, noActivate: true, rounded: true);
        _pause = Toggle("", "Пауза", () => shots.TogglePause());
        _mic = Toggle("", "Микрофон", () => shots.SetMic(!shots.RecordMic));
        _sound = Toggle("", "Звук системы", () => shots.SetSystemSound(!shots.RecordSystemSound));
        var stop = Toggle("", "Остановить (Shift+PrtSc)", () => shots.StopRecording());
        stop.Foreground = Theme.Brush(Windows.UI.Color.FromArgb(255, 0xFF, 0x5B, 0x5B));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(10, 5, 6, 5) };
        row.Children.Add(_dot);
        row.Children.Add(new Border { Width = 4 });
        row.Children.Add(_time);
        row.Children.Add(_pause);
        row.Children.Add(_mic);
        row.Children.Add(_sound);
        row.Children.Add(new Border { Width = 1, Height = 20, Background = Theme.Brush(Theme.Separator), Margin = new Thickness(2, 0, 2, 0) });
        row.Children.Add(stop);
        _panel.Root.Children.Add(row);
        SetWindowDisplayAffinity(_panel.Hwnd, WDA_EXCLUDEFROMCAPTURE);

        Update();
        row.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = (int)Math.Ceiling((row.DesiredSize.Width + 4) * screen.Scale);
        var h = (int)Math.Ceiling(42 * screen.Scale);
        var gap = (int)(10 * screen.Scale);
        var x = Math.Clamp((region.Left + region.Right - w) / 2, screen.Bounds.Left + gap, screen.Bounds.Right - gap - w);
        // Под областью, если там есть место (над доком), иначе над ней, иначе внутри сверху.
        int y;
        if (region.Bottom + gap + h <= screen.Bounds.Bottom - (int)(80 * screen.Scale)) y = region.Bottom + gap;
        else if (region.Top - gap - h >= screen.Bounds.Top) y = region.Top - gap - h;
        else y = region.Top + gap;
        _panel.Place(x, y, w, h);
        _panel.Show(activate: false);
    }

    Button Toggle(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontFamily = Theme.Icons, FontSize = 14 },
            Width = 32, Height = 30, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            AllowFocusOnInteraction = false,
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// Таймер и вид кнопок (раз в секунду и после нажатий).
    public void Update()
    {
        var t = _shots.RecordingTime;
        _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        var paused = _shots.RecordingPaused;
        _dot.Opacity = paused ? 0.35 : 1;
        ((FontIcon)_pause.Content).Glyph = paused ? "" : "";
        ToolTipService.SetToolTip(_pause, paused ? "Продолжить" : "Пауза");
        Look(_mic, _shots.RecordMic, "Микрофон");
        Look(_sound, _shots.RecordSystemSound, "Звук системы");
    }

    static void Look(Button b, bool on, string name)
    {
        b.Background = Theme.Brush(on ? Theme.Selected : Microsoft.UI.Colors.Transparent);
        b.Opacity = on ? 1 : 0.45;
        ToolTipService.SetToolTip(b, $"{name}: {(on ? "пишется" : "выключен")}");
    }

    public void Close()
    {
        foreach (var e in _edges) DestroyWindow(e);
        _edges.Clear();
        _panel.Hide();
        _panel.Window.Close();
    }

    void Edge(int x, int y, int w, int h)
    {
        Register();
        var hwnd = CreateWindowExW((uint)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */,
            BorderClass, "", 0x80000000 /* WS_POPUP */, x, y, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return;
        SetLayeredWindowAttributes(hwnd, 0, 255, 2 /* LWA_ALPHA */);
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _edges.Add(hwnd);
    }

    static void Register()
    {
        if (_registered) return;
        _proc = DefWindowProcW;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandleW(null),
            hbrBackground = Gdi.CreateSolidBrush(Gdi.Rgb(0xFF, 0x5B, 0x5B)),
            lpszClassName = BorderClass,
        };
        RegisterClassExW(ref wc);
        _registered = true;
    }

    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
