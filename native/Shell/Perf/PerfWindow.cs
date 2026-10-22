using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WindowsShell.Config;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Perf;

/// Окно «Монитор ПК» (design-v2 п. 8б): всё сразу — FPS (если идёт игра), процессор, видеокарта,
/// память, видеопамять, сеть — с графиками за последнюю минуту. Без рамки и заголовка, стекло,
/// тянется мышью за любое место, меняет размер, помнит место. Поверх окон или обычным окном.
public sealed class PerfWindow
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    static readonly Color Line = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    static readonly Color Muted = Color.FromArgb(255, 0xA9, 0xAD, 0xB4);
    static readonly Color Up = Color.FromArgb(255, 0x6C, 0xCB, 0x5F);

    readonly PerfService _perf;
    readonly ShellConfig _config;
    readonly Window _window;
    readonly IntPtr _hwnd;
    readonly Grid _root;
    readonly OverlappedPresenter _presenter;
    readonly Grid _cards = new() { ColumnSpacing = 10, RowSpacing = 10 };
    readonly TextBlock _game = Text(12, Muted);
    readonly Card _fps, _cpu, _gpu, _ram, _vram, _net;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _save;
    readonly Microsoft.UI.Input.InputNonClientPointerSource _caption;
    readonly SubclassProc _proc;
    bool _closing, _narrow = true;

    /// Окно закрыли (крестиком меню, Alt+F4 или выключили в настройках).
    public event Action? Closed;

    public PerfWindow(PerfService perf, ShellConfig config)
    {
        _perf = perf;
        _config = config;
        _window = new Window { Title = "Монитор ПК" };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        Apps.OwnWindows.Register(_hwnd, "perf", "Монитор ПК", "\uE9D9");
        _fps = new Card("FPS", wide: true);
        _cpu = new Card("Процессор");
        _gpu = new Card("Видеокарта");
        _ram = new Card("Память");
        _vram = new Card("Видеопамять");
        _net = new Card("Сеть", wide: true, second: true);

        var head = new Grid { Margin = new Thickness(2, 0, 2, 2) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Text(13, Muted, "Монитор ПК"));
        Grid.SetColumn(_game, 1);
        head.Children.Add(_game);
        var stack = new StackPanel { Spacing = 10, Padding = new Thickness(14, 12, 14, 14), Children = { head, _cards } };
        _root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Children = { new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden } },
        };
        _window.Content = _root;
        Layout(narrow: false);
        _root.SizeChanged += (_, e) => Layout(e.NewSize.Width < 380);

        ApplySurface();
        Glass.Changed += ApplySurface;
        _presenter = OverlappedPresenter.Create();
        _presenter.IsMaximizable = false;
        _presenter.IsResizable = true;
        // Рамка остаётся (за неё тянут размер), заголовка нет.
        _presenter.SetBorderAndTitleBar(true, false);
        _window.AppWindow.SetPresenter(_presenter);
        SetDark(_hwnd);
        SetCorner(_hwnd, DWMWCP_ROUND);
        try { _window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "shell.ico")); } catch { }

        // Тянуть окно можно за любое место: всё окно — заголовок, его двигает сама Windows.
        // WM_NCLBUTTONDOWN из PointerPressed трогался с места только через секунду-две (WSH-125).
        _caption = InputNonClientPointerSource.GetForWindowId(_window.AppWindow.Id);
        // Правый клик по заголовку — системное меню окна; показываем своё.
        _proc = (h, msg, w, l, _, _) =>
        {
            if (msg != 0x007B /* WM_CONTEXTMENU */) return DefProc(h, msg, w, l);
            GetCursorPos(out var p);
            _window.DispatcherQueue.TryEnqueue(() => _perf.ShowMenu(p.X, p.Y, Menu()));
            return IntPtr.Zero;
        };
        Subclass(_hwnd, _proc, 2);

        _save = _window.DispatcherQueue.CreateTimer();
        _save.Interval = TimeSpan.FromSeconds(1);
        _save.IsRepeating = false;
        _save.Tick += (_, _) => SaveRect();
        _window.AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange) Caption();
            if (e.DidPositionChange || e.DidSizeChange) { _save.Stop(); _save.Start(); }
        };
        _window.Closed += (_, _) =>
        {
            Glass.Changed -= ApplySurface;
            _perf.Updated -= Refresh;
            if (_save.IsRunning) SaveRect();
            Closed?.Invoke();
            // Закрыли не из настроек (Alt+F4) — выключаем и там.
            if (!_closing) _config.Update(_ => _perf.Settings.Window = false);
        };

        Place();
        Caption();
        Apply(perf.Settings);
        _perf.Updated += Refresh;
        Refresh();
        _window.AppWindow.Show(false);
        Log.Write("окно «Монитор ПК» открыто");
    }

    public void Apply(PerfSettings s) => _presenter.IsAlwaysOnTop = s.WindowTopmost;

    public void Close()
    {
        _closing = true;
        _window.Close();
    }

    List<MenuItem> Menu() =>
    [
        new() { Text = "Поверх всех окон", Checked = _perf.Settings.WindowTopmost, Invoke = () => _config.Update(_ => _perf.Settings.WindowTopmost = !_perf.Settings.WindowTopmost) },
        new() { Text = "Настройки…", Glyph = "", Invoke = _perf.OpenSettings },
        MenuItem.Sep,
        new() { Text = "Закрыть", Glyph = "", Invoke = () => _config.Update(_ => _perf.Settings.Window = false) },
    ];

    void ApplySurface()
    {
        if (Glass.Tint)
        {
            _window.SystemBackdrop = null;
            _root.Background = Theme.Brush(Theme.TintMenu);
        }
        else
        {
            if (_window.SystemBackdrop is not AlwaysActiveAcrylic) _window.SystemBackdrop = new AlwaysActiveAcrylic();
            _root.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
        }
    }

    /// Сохранённое место, если оно на каком-то мониторе; иначе справа на мониторе с курсором.
    void Place()
    {
        if (_perf.Settings.WindowRect is { } r && r.W > 100 && r.H > 100)
        {
            var center = new POINT { X = r.X + r.W / 2, Y = r.Y + 20 };
            if (MonitorFromPoint(center, 0 /* MONITOR_DEFAULTTONULL */) != IntPtr.Zero)
            {
                _window.AppWindow.MoveAndResize(new RectInt32(r.X, r.Y, r.W, r.H));
                return;
            }
        }
        GetCursorPos(out var p);
        var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var work = MonitorInfo(mon).rcWork;
        var scale = MonitorScale(mon);
        var w = (int)(560 * scale);
        var h = (int)(470 * scale);
        var m = (int)(24 * scale);
        _window.AppWindow.MoveAndResize(new RectInt32(work.Right - w - m, work.Top + m, w, h));
    }

    void Caption()
    {
        var s = _window.AppWindow.ClientSize;
        _caption.SetRegionRects(NonClientRegionKind.Caption, [new RectInt32(0, 0, s.Width, s.Height)]);
    }

    void SaveRect()
    {
        _save.Stop();
        if (!GetWindowRect(_hwnd, out var r) || r.Width < 100 || r.Height < 100) return;
        var rect = (r.Left, r.Top, r.Width, r.Height);
        if (_perf.Settings.WindowRect == rect) return;
        _config.Update(_ => _perf.Settings.WindowRect = rect);
    }

    /// Две колонки карточек, в узком окне — одна.
    void Layout(bool narrow)
    {
        if (narrow == _narrow) return;
        _narrow = narrow;
        _cards.Children.Clear();
        _cards.ColumnDefinitions.Clear();
        _cards.RowDefinitions.Clear();
        var cols = narrow ? 1 : 2;
        for (var c = 0; c < cols; c++) _cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0, col = 0;
        foreach (var card in new[] { _fps, _cpu, _gpu, _ram, _vram, _net })
        {
            var span = card.Wide ? cols : 1;
            if (col + span > cols) { row++; col = 0; }
            while (_cards.RowDefinitions.Count <= row) _cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(card.Root, row);
            Grid.SetColumn(card.Root, col);
            Grid.SetColumnSpan(card.Root, span);
            _cards.Children.Add(card.Root);
            col += span;
            if (col >= cols) { row++; col = 0; }
        }
    }

    void Refresh()
    {
        var now = _perf.Now;
        var s = now.Sample;
        var hist = _perf.History;
        _game.Text = now.Game is { } g ? $"Игра: {g}" : "";

        if (now.Game is null) _fps.Set("—", "нет игры", "", null);
        else if (now.Fps is { } f)
            _fps.Set($"{f:0}", $"{1000 / f:0.0} мс на кадр".Replace('.', ','), now.Low1 is { } l ? $"1% low {l:0}" : "", null);
        else _fps.Set("—", _perf.FpsDenied ? "нужно разрешение (настройки)" : "кадров нет", "", null);
        _fps.Spark(_perf.FpsHistory.Select(v => v ?? 0).ToList(), null);

        if (s is null)
        {
            foreach (var c in new[] { _cpu, _gpu, _ram, _vram, _net }) c.Set("—", "", "", null);
            return;
        }
        _cpu.Set($"{s.Cpu:0}%", "", Temp(s.CpuTemp), Overlay.TempColor(s.CpuTemp));
        _cpu.Spark(hist.Select(x => x.Cpu).ToList(), 100);
        _gpu.Set(s.Gpu is { } gp ? $"{gp:0}%" : "—", "", Temp(s.GpuTemp), Overlay.TempColor(s.GpuTemp));
        _gpu.Spark(hist.Select(x => x.Gpu ?? 0).ToList(), 100);
        _ram.Set(Overlay.Gb(s.RamUsed), $"из {Overlay.Gb(s.RamTotal, whole: true)} ГБ", $"{100 * s.RamUsed / s.RamTotal:0}%", null);
        _ram.Spark(hist.Select(x => 100 * x.RamUsed / x.RamTotal).ToList(), 100);
        if (s.VramUsed is { } vu && s.VramTotal is { } vt && vt > 0)
        {
            _vram.Set(Overlay.Gb(vu), $"из {Overlay.Gb(vt, whole: true)} ГБ", $"{100 * vu / vt:0}%", null);
            _vram.Spark(hist.Select(x => x.VramUsed is { } a && x.VramTotal is { } b && b > 0 ? 100 * a / b : 0).ToList(), 100);
        }
        else _vram.Set("—", "", "", null);
        _net.Set($"↓ {Speed(s.NetDown)}", $"↑ {Speed(s.NetUp)}", "", null);
        _net.Spark(hist.Select(x => x.NetDown).ToList(), null, hist.Select(x => x.NetUp).ToList());
    }

    static string Temp(double? t) => t is { } v ? $"{v:0}°" : "";

    static string Speed(double bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / 1024 / 1024).ToString("0.0", Ru) + " МБ/с",
        _ => (bytes / 1024).ToString("0", Ru) + " КБ/с",
    };

    static TextBlock Text(double size, Color color, string text = "") => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        Foreground = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// Карточка: заголовок и подпись справа, большое число с пояснением, график за минуту.
    sealed class Card
    {
        public Border Root { get; }
        public bool Wide { get; }
        readonly TextBlock _right = Text(12, Muted);
        readonly TextBlock _value = Text(24, Microsoft.UI.Colors.White);
        readonly TextBlock _small = Text(13, Muted);
        readonly Grid _spark = new() { Height = 38 };
        readonly Polyline _line = new() { StrokeThickness = 1.6, Stroke = new SolidColorBrush(Line), StrokeLineJoin = PenLineJoin.Round };
        readonly Polygon _fill = new() { Fill = new SolidColorBrush(Color.FromArgb(0x24, 0x4C, 0xC2, 0xFF)) };
        readonly Polyline _line2 = new() { StrokeThickness = 1.4, Stroke = new SolidColorBrush(Up), StrokeLineJoin = PenLineJoin.Round };
        List<double> _a = [], _b = [];
        double? _max;

        public Card(string title, bool wide = false, bool second = false)
        {
            Wide = wide;
            _value.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(Text(12, Muted, title));
            Grid.SetColumn(_right, 1);
            head.Children.Add(_right);
            var value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _value, _small } };
            _small.VerticalAlignment = VerticalAlignment.Bottom;
            _small.Margin = new Thickness(0, 0, 0, 4);
            _spark.Children.Add(_fill);
            _spark.Children.Add(_line);
            if (second) _spark.Children.Add(_line2);
            _spark.SizeChanged += (_, _) => Draw();
            Root = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x0A, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x17, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Child = new StackPanel { Spacing = 4, Children = { head, value, _spark } },
            };
        }

        public void Set(string value, string small, string right, System.Drawing.Color? rightColor)
        {
            _value.Text = value;
            _small.Text = small;
            _right.Text = right;
            _right.Foreground = new SolidColorBrush(rightColor is { } c ? Color.FromArgb(255, c.R, c.G, c.B) : Muted);
        }

        /// График: `max` — верх шкалы (null — по самому большому значению).
        public void Spark(List<double> a, double? max, List<double>? b = null)
        {
            _a = a;
            _b = b ?? [];
            _max = max;
            Draw();
        }

        void Draw()
        {
            var w = _spark.ActualWidth;
            var h = _spark.ActualHeight;
            _line.Points.Clear();
            _fill.Points.Clear();
            _line2.Points.Clear();
            if (w <= 0 || h <= 0 || _a.Count < 2) return;
            var top = _max ?? Math.Max(1, Math.Max(_a.Max(), _b.Count > 0 ? _b.Max() : 0) * 1.15);
            var step = w / (PerfService.HistorySeconds - 1);
            // Свежее справа: история короче минуты — график начинается не с левого края.
            var x0 = w - step * (_a.Count - 1);
            for (var i = 0; i < _a.Count; i++)
                _line.Points.Add(new Windows.Foundation.Point(x0 + i * step, h - 1 - (h - 2) * Math.Clamp(_a[i] / top, 0, 1)));
            _fill.Points.Add(new Windows.Foundation.Point(x0, h));
            foreach (var p in _line.Points) _fill.Points.Add(p);
            _fill.Points.Add(new Windows.Foundation.Point(w, h));
            var x1 = w - step * (_b.Count - 1);
            for (var i = 0; i < _b.Count; i++)
                _line2.Points.Add(new Windows.Foundation.Point(x1 + i * step, h - 1 - (h - 2) * Math.Clamp(_b[i] / top, 0, 1)));
        }
    }
}
