using System.Collections.Immutable;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.System;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using D = System.Drawing;
using Color = Windows.UI.Color;

namespace WindowsShell.Shots;

/// Редактор снимка (design-v2 п. 4): клик по миниатюре или «Изменить». Инструменты с буквами:
/// стрелка A, рамка R, маркер H, текст T, размытие B, номер N, обрезка C; пять цветов и толщина.
/// Ctrl+Z / Ctrl+Y, Ctrl+C копировать, Ctrl+S сохранить, Enter — сохранить, скопировать и
/// закрыть, Esc — закрыть без изменений. Готовую картинку можно тянуть за «Тяни» в любое окно.
public sealed class ShotEditor
{
    enum Tool { Arrow, Rect, Marker, Text, Blur, Number, Crop }

    static readonly (Tool Tool, string Glyph, string Name, VirtualKey Key)[] Tools =
    [
        (Tool.Arrow, "➚", "Стрелка", VirtualKey.A),
        (Tool.Rect, "▢", "Рамка", VirtualKey.R),
        (Tool.Marker, "▌", "Маркер", VirtualKey.H),
        (Tool.Text, "T", "Текст", VirtualKey.T),
        (Tool.Blur, "▦", "Размытие", VirtualKey.B),
        (Tool.Number, "①", "Номер", VirtualKey.N),
        (Tool.Crop, "⌗", "Обрезка", VirtualKey.C),
    ];

    static readonly D.Color[] Colors =
    [
        D.Color.FromArgb(0xFF, 0x5B, 0x5B),
        D.Color.FromArgb(0xFF, 0xC8, 0x3D),
        D.Color.FromArgb(0x6C, 0xCB, 0x5F),
        D.Color.FromArgb(0x4C, 0xC2, 0xFF),
        D.Color.FromArgb(0xFF, 0xFF, 0xFF),
    ];

    static readonly Dictionary<string, ShotEditor> Opened = new(StringComparer.OrdinalIgnoreCase);

    // Последний выбор помнится между снимками.
    static Tool _lastTool = Tool.Arrow;
    static int _lastColor, _lastSize = 1;

    readonly ShotsService _shots;
    readonly ShotItem _item;
    readonly Window _window;
    readonly IntPtr _hwnd;
    readonly D.Bitmap _source;
    readonly List<EditState> _history = [EditState.Empty];
    int _at;
    bool _saved = true;

    readonly ContentControl _host = new() { IsTabStop = true, UseSystemFocusVisuals = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Grid _paper = new();
    readonly Grid _frame = new();
    readonly Image _view = new() { Stretch = Stretch.Fill };
    readonly Canvas _live = new() { Background = Theme.Brush(Microsoft.UI.Colors.Transparent) };
    readonly TextBlock _status = new() { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextTertiary), VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<Tool, Button> _toolButtons = [];
    readonly List<Border> _swatches = [];
    readonly List<Button> _sizes = [];

    Tool _tool = _lastTool;
    int _color = _lastColor, _size = _lastSize;
    double _scale = 1;

    // Что сейчас рисуем мышью.
    bool _drawing;
    D.PointF _from;
    readonly List<D.PointF> _points = [];
    Shape? _shape;
    Polygon? _head;
    TextBox? _text;
    D.PointF _textAt;

    EditState State => _history[_at];

    D.Size Shown => State.Crop is { } c ? c.Size : _source.Size;

    D.Point Offset => State.Crop is { } c ? c.Location : D.Point.Empty;

    public static void Open(ShotsService shots, ShotItem item)
    {
        if (Opened.TryGetValue(item.Path, out var open))
        {
            open._window.Activate();
            ShellWindow.ForceForeground(open._hwnd);
            return;
        }
        D.Bitmap source;
        try
        {
            // Читаем в память: файл не держим, его можно перезаписать при сохранении.
            using var stream = new MemoryStream(File.ReadAllBytes(item.Path));
            using var image = D.Image.FromStream(stream);
            source = new D.Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using var g = D.Graphics.FromImage(source);
            g.DrawImageUnscaled(image, 0, 0);
        }
        catch (Exception e)
        {
            Log.Error($"редактор: не открылся {item.Name}", e);
            return;
        }
        Opened[item.Path] = new ShotEditor(shots, item, source);
    }

    ShotEditor(ShotsService shots, ShotItem item, D.Bitmap source)
    {
        _shots = shots;
        _item = item;
        _source = source;
        _window = new Window { Title = "Снимок — Windows Shell" };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(Toolbar());

        // Картинка 1:1 (в физических пикселях), а если не влезает — уменьшается целиком.
        _paper.Children.Add(_view);
        _paper.Children.Add(_live);
        _frame.Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = _paper });
        var area = new Border
        {
            Background = Theme.Brush(Color.FromArgb(255, 0x16, 0x18, 0x1B)),
            Padding = new Thickness(16),
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = _frame },
        };
        Grid.SetRow(area, 1);
        root.Children.Add(area);
        _paper.PointerPressed += OnPressed;
        _paper.PointerMoved += OnMoved;
        _paper.PointerReleased += OnReleased;
        _paper.PointerCaptureLost += (_, _) => { if (_drawing) Cancel(); };

        var status = new Grid { Padding = new Thickness(14, 6, 14, 6), Background = Theme.Brush(Theme.Card) };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Children.Add(_status);
        var keys = new TextBlock { Text = "Ctrl+Z отменить · Enter готово · Esc закрыть", FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextTertiary) };
        Grid.SetColumn(keys, 1);
        status.Children.Add(keys);
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        _host.Content = root;
        _host.KeyDown += OnKey;
        _window.Content = _host;
        if (Glass.Tint) root.Background = Theme.Brush(Theme.TintDock);
        else _window.SystemBackdrop = new MicaBackdrop();

        var presenter = OverlappedPresenter.Create();
        // Панели задач Windows нет, а наши окна док не показывает: свёрнутое не найти.
        presenter.IsMinimizable = false;
        _window.AppWindow.SetPresenter(presenter);
        SetDark(_hwnd);
        try { _window.AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "shell.ico")); } catch { }
        _window.Closed += (_, _) =>
        {
            Opened.Remove(_item.Path);
            _source.Dispose();
        };

        _scale = Place();
        Refresh();
        SelectTool(_tool);
        UpdateSwatches();
        _host.Loaded += (_, _) =>
        {
            _host.Focus(FocusState.Programmatic);
            FitToolbar();
        };
        _window.Activate();
        ShellWindow.ForceForeground(_hwnd);
        _host.Focus(FocusState.Programmatic);
        Log.Write($"редактор: {_item.Name} {_source.Width}×{_source.Height}");
    }

    /// По центру монитора с курсором: картинка 1:1, если влезает, иначе 90 % экрана. Масштаб монитора.
    double Place()
    {
        GetCursorPos(out var p);
        var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var work = MonitorInfo(mon).rcWork;
        var scale = MonitorScale(mon);
        var chrome = (int)((32 + 100 + 32 + 32) * scale);
        var w = Math.Clamp(_source.Width + (int)(32 * scale) + 16, (int)(940 * scale), (int)(work.Width * 0.9));
        var h = Math.Clamp(_source.Height + chrome, (int)(480 * scale), (int)(work.Height * 0.9));
        _window.AppWindow.MoveAndResize(new RectInt32(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h));
        return scale;
    }

    /// Панель шире окна (крупный шрифт, другой масштаб) — расширить окно, чтобы видно было все кнопки.
    void FitToolbar()
    {
        if (_bar is null) return;
        _bar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var need = (int)Math.Ceiling(_bar.DesiredSize.Width * _scale);
        var client = _window.AppWindow.ClientSize.Width;
        if (need <= client) return;
        var mon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var work = MonitorInfo(mon).rcWork;
        var size = _window.AppWindow.Size;
        var w = Math.Min(size.Width + need - client, work.Width);
        var pos = _window.AppWindow.Position;
        var x = Math.Clamp(pos.X - (w - size.Width) / 2, work.Left, work.Right - w);
        _window.AppWindow.MoveAndResize(new RectInt32(x, pos.Y, w, size.Height));
    }

    // ---------- панель инструментов ----------

    Grid? _bar;

    UIElement Toolbar()
    {
        // Два ряда: сверху инструменты, снизу цвет, толщина и действия. В один ряд всё не влезало
        // в окно обычной ширины, и правые кнопки обрезались.
        var bar = new Grid { Padding = new Thickness(10, 8, 10, 8), ColumnSpacing = 4, RowSpacing = 6, Background = Theme.Brush(Theme.Card) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _bar = bar;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        Grid.SetColumnSpan(tools, 3);
        bar.Children.Add(tools);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(2, 0, 0, 0) };
        Grid.SetRow(left, 1);
        foreach (var (tool, glyph, name, key) in Tools)
        {
            var t = tool;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = name, FontFamily = Theme.Font, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new Border
            {
                BorderBrush = Theme.Brush(Theme.Stroke), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = key.ToString(), FontFamily = Theme.Font, FontSize = 11, Foreground = Theme.Brush(Theme.TextTertiary) },
            });
            var b = ToolbarButton(content, () => SelectTool(t));
            ToolTipService.SetToolTip(b, $"{name} ({key})");
            _toolButtons[tool] = b;
            tools.Children.Add(b);
        }
        for (var i = 0; i < Colors.Length; i++)
        {
            var index = i;
            var c = Colors[i];
            var dot = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = Theme.Brush(Color.FromArgb(255, c.R, c.G, c.B)),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.PointerPressed += (_, e) => { e.Handled = true; _color = _lastColor = index; UpdateSwatches(); RestyleText(); };
            _swatches.Add(dot);
            left.Children.Add(dot);
        }
        left.Children.Add(Divider());
        for (var i = 0; i < 3; i++)
        {
            var size = i;
            var d = 4 + i * 4;
            var b = ToolbarButton(new Border { Width = d, Height = d, CornerRadius = new CornerRadius(d / 2.0), Background = Theme.Brush(Theme.Text) }, () =>
            {
                _size = _lastSize = size;
                UpdateSwatches();
                RestyleText();
            });
            b.Width = 32;
            ToolTipService.SetToolTip(b, i switch { 0 => "Тонко", 1 => "Средне", _ => "Толсто" });
            _sizes.Add(b);
            left.Children.Add(b);
        }
        bar.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var grip = new Border
        {
            Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Theme.Brush(Theme.Card),
            BorderBrush = Theme.Brush(Theme.Stroke), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "✥ Тяни в окно", FontFamily = Theme.Font, FontSize = 13 },
        };
        ToolTipService.SetToolTip(grip, "Потяни отсюда в чат или папку: картинка уйдёт файлом");
        var gripPressed = false;
        var gripAt = new Windows.Foundation.Point();
        grip.PointerPressed += (_, e) =>
        {
            gripPressed = true;
            gripAt = e.GetCurrentPoint(grip).Position;
            grip.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!gripPressed) return;
            var point = e.GetCurrentPoint(grip);
            var p = point.Position;
            if (Math.Abs(p.X - gripAt.X) + Math.Abs(p.Y - gripAt.Y) < 6) return;
            gripPressed = false;
            grip.ReleasePointerCaptures();
            if (Save()) FileDrag.Start(grip, point, _item.Path);
        };
        grip.PointerReleased += (_, _) => { gripPressed = false; grip.ReleasePointerCaptures(); };
        right.Children.Add(grip);
        right.Children.Add(TextButton("Копировать", Copy, "Ctrl+C"));
        right.Children.Add(TextButton("Сохранить как…", () => _ = SaveAs(), null));
        var done = TextButton("Готово ⏎", Done, "Enter: сохранить, скопировать и закрыть");
        done.Background = Theme.Brush(Theme.Accent);
        done.Foreground = Theme.Brush(Color.FromArgb(255, 0x06, 0x23, 0x3A));
        right.Children.Add(done);
        Grid.SetColumn(right, 2);
        Grid.SetRow(right, 1);
        bar.Children.Add(right);
        return bar;
    }

    static Border Divider() => new() { Width = 1, Height = 22, Background = Theme.Brush(Theme.Separator), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    /// Кнопки не забирают фокус: клавиши-буквы остаются у редактора.
    static Button ToolbarButton(UIElement content, Action click)
    {
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(9, 5, 9, 5),
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Brush(Microsoft.UI.Colors.Transparent),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => click();
        return b;
    }

    static Button TextButton(string text, Action click, string? tip)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontFamily = Theme.Font, FontSize = 13 },
            Padding = new Thickness(12, 5, 12, 6),
            Height = 34,
            CornerRadius = new CornerRadius(6),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    void SelectTool(Tool tool)
    {
        if (_text != null) CommitText();
        _tool = tool;
        if (tool != Tool.Crop) _lastTool = tool;
        foreach (var (t, b) in _toolButtons)
        {
            var on = t == tool;
            b.Background = Theme.Brush(on ? Color.FromArgb(0x2E, 0x4C, 0xC2, 0xFF) : Microsoft.UI.Colors.Transparent);
            b.BorderBrush = Theme.Brush(on ? Color.FromArgb(0x8C, 0x4C, 0xC2, 0xFF) : Microsoft.UI.Colors.Transparent);
        }
    }

    void UpdateSwatches()
    {
        for (var i = 0; i < _swatches.Count; i++)
            _swatches[i].BorderBrush = Theme.Brush(i == _color ? Theme.Text : Microsoft.UI.Colors.Transparent);
        for (var i = 0; i < _sizes.Count; i++)
            _sizes[i].Background = Theme.Brush(i == _size ? Theme.Selected : Microsoft.UI.Colors.Transparent);
    }

    // ---------- размеры пометок (в пикселях снимка, крупнее на мониторах с масштабом) ----------

    float K => (float)Math.Max(1, _scale);
    D.Color Pen => Colors[_color];
    float StrokeW => K * _size switch { 0 => 2.5f, 1 => 4f, _ => 7f };
    float MarkerW => K * _size switch { 0 => 12f, 1 => 20f, _ => 32f };
    float FontPx => K * _size switch { 0 => 16f, 1 => 22f, _ => 32f };
    float Radius => K * _size switch { 0 => 11f, 1 => 14f, _ => 19f };
    int Block => (int)(K * _size switch { 0 => 6, 1 => 10, _ => 16 });

    static Color Ui(D.Color c, byte? alpha = null) => Color.FromArgb(alpha ?? c.A, c.R, c.G, c.B);

    // ---------- рисование мышью ----------

    D.PointF At(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_paper).Position;
        var s = Shown;
        return new D.PointF((float)Math.Clamp(p.X, 0, s.Width), (float)Math.Clamp(p.Y, 0, s.Height));
    }

    /// Точка на видимой картинке → точка исходного снимка (с учётом обрезки).
    D.PointF Src(D.PointF p) => new(p.X + Offset.X, p.Y + Offset.Y);

    void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(_paper).Properties;
        if (!props.IsLeftButtonPressed) return;
        e.Handled = true;
        var p = At(e);
        if (_text != null) { CommitText(); return; }
        switch (_tool)
        {
            case Tool.Number:
                Push(State with { Marks = State.Marks.Add(new NumberMark(Src(p), State.NextNumber, Pen, Radius)) });
                return;
            case Tool.Text:
                StartText(p);
                return;
        }
        _drawing = true;
        _from = p;
        _points.Clear();
        _points.Add(p);
        _paper.CapturePointer(e.Pointer);
        _shape = _tool switch
        {
            Tool.Arrow => new Line { Stroke = Theme.Brush(Ui(Pen)), StrokeThickness = StrokeW, StrokeStartLineCap = PenLineCap.Round },
            Tool.Rect => new Rectangle { Stroke = Theme.Brush(Ui(Pen)), StrokeThickness = StrokeW },
            Tool.Marker => new Polyline { Stroke = Theme.Brush(Ui(MarkPainter.MarkerColor(Pen))), StrokeThickness = MarkerW, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
            _ => new Rectangle
            {
                Stroke = Theme.Brush(Theme.Text), StrokeThickness = Math.Max(1, K),
                StrokeDashArray = [4, 3],
                Fill = Theme.Brush(Color.FromArgb(_tool == Tool.Crop ? (byte)0x18 : (byte)0x40, 0, 0, 0)),
            },
        };
        _live.Children.Add(_shape);
        if (_tool == Tool.Arrow)
        {
            _head = new Polygon { Fill = Theme.Brush(Ui(Pen)) };
            _live.Children.Add(_head);
        }
        UpdateShape(p);
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing) return;
        var p = At(e);
        if (_tool == Tool.Marker) _points.Add(p);
        UpdateShape(p);
    }

    void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing) return;
        var p = At(e);
        _drawing = false;
        _paper.ReleasePointerCaptures();
        ClearLive();
        var rect = RectOf(_from, p);
        var tiny = rect.Width < 3 && rect.Height < 3;
        Mark? mark = _tool switch
        {
            Tool.Arrow when !tiny => new ArrowMark(Src(_from), Src(p), Pen, StrokeW),
            Tool.Rect when !tiny => new RectMark(Shift(rect), Pen, StrokeW),
            Tool.Marker => new MarkerMark([.. _points.Select(Src)], Pen, MarkerW),
            Tool.Blur when !tiny => new BlurMark(D.Rectangle.Round(Shift(rect)), Block),
            _ => null,
        };
        if (mark != null) { Push(State with { Marks = State.Marks.Add(mark) }); return; }
        if (_tool == Tool.Crop && rect.Width >= 8 && rect.Height >= 8)
        {
            Push(State with { Crop = D.Rectangle.Round(Shift(rect)) });
            // Обрезали — дальше рисуем тем, чем рисовали.
            SelectTool(_lastTool);
        }
    }

    D.RectangleF Shift(D.RectangleF r) => new(r.X + Offset.X, r.Y + Offset.Y, r.Width, r.Height);

    static D.RectangleF RectOf(D.PointF a, D.PointF b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    void UpdateShape(D.PointF p)
    {
        switch (_shape)
        {
            case Line line:
            {
                var dx = p.X - _from.X;
                var dy = p.Y - _from.Y;
                var len = MathF.Sqrt(dx * dx + dy * dy);
                var (hl, hw) = MarkPainter.Head(StrokeW);
                if (hl > len) { hw *= len / Math.Max(1, hl); hl = len; }
                var ux = len > 0 ? dx / len : 0;
                var uy = len > 0 ? dy / len : 0;
                var bx = p.X - ux * hl;
                var by = p.Y - uy * hl;
                line.X1 = _from.X; line.Y1 = _from.Y;
                line.X2 = bx + ux * hl * 0.3; line.Y2 = by + uy * hl * 0.3;
                if (_head != null)
                    _head.Points = [new(p.X, p.Y), new(bx - uy * hw, by + ux * hw), new(bx + uy * hw, by - ux * hw)];
                break;
            }
            case Polyline poly:
                poly.Points = [.. _points.Select(q => new Windows.Foundation.Point(q.X, q.Y))];
                break;
            case Rectangle r:
            {
                var rect = RectOf(_from, p);
                Canvas.SetLeft(r, rect.X);
                Canvas.SetTop(r, rect.Y);
                r.Width = rect.Width;
                r.Height = rect.Height;
                break;
            }
        }
    }

    void Cancel()
    {
        _drawing = false;
        ClearLive();
    }

    void ClearLive()
    {
        _live.Children.Clear();
        _shape = null;
        _head = null;
        if (_text != null) _live.Children.Add(_text);
    }

    // ---------- текст ----------

    void StartText(D.PointF p)
    {
        _textAt = p;
        var box = new TextBox
        {
            FontFamily = new FontFamily(MarkPainter.FontFace),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            MinWidth = 60,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
            AcceptsReturn = false,
        };
        // Без белой подложки и толстой линии снизу, как у обычного поля ввода.
        foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBackground" })
            box.Resources[key] = Theme.Brush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "TextControlBorderBrushFocused", "TextControlBorderBrushPointerOver", "TextControlBorderBrush" })
            box.Resources[key] = Theme.Brush(Color.FromArgb(0x99, 0x4C, 0xC2, 0xFF));
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        Canvas.SetLeft(box, p.X - 1);
        Canvas.SetTop(box, p.Y - 1);
        _text = box;
        RestyleText();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; CommitText(); }
            else if (e.Key == VirtualKey.Escape) { e.Handled = true; DropText(); }
        };
        _live.Children.Add(box);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
    }

    void RestyleText()
    {
        if (_text is null) return;
        _text.FontSize = FontPx;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundFocused", "TextControlForegroundPointerOver" })
            _text.Resources[key] = Theme.Brush(Ui(Pen));
        _text.Foreground = Theme.Brush(Ui(Pen));
    }

    void CommitText()
    {
        if (_text is not { } box) return;
        var text = box.Text.Trim();
        DropText();
        if (text.Length > 0) Push(State with { Marks = State.Marks.Add(new TextMark(Src(_textAt), text, Pen, FontPx)) });
    }

    void DropText()
    {
        if (_text is null) return;
        _live.Children.Remove(_text);
        _text = null;
        _host.Focus(FocusState.Programmatic);
    }

    // ---------- история и картинка ----------

    void Push(EditState next)
    {
        if (_at < _history.Count - 1) _history.RemoveRange(_at + 1, _history.Count - _at - 1);
        _history.Add(next);
        _at = _history.Count - 1;
        _saved = false;
        Refresh();
    }

    void Undo()
    {
        if (_text != null) { DropText(); return; }
        if (_at == 0) return;
        _at--;
        _saved = false;
        Refresh();
    }

    void Redo()
    {
        if (_at >= _history.Count - 1) return;
        _at++;
        _saved = false;
        Refresh();
    }

    /// Перерисовать картинку со всеми пометками.
    void Refresh()
    {
        using var bmp = MarkPainter.Render(_source, State);
        _view.Source = ToWriteable(bmp);
        _paper.Width = bmp.Width;
        _paper.Height = bmp.Height;
        _frame.Width = bmp.Width / _scale;
        _frame.Height = bmp.Height / _scale;
        _status.Text = $"{bmp.Width} × {bmp.Height} · {_item.Name}";
    }

    static WriteableBitmap ToWriteable(D.Bitmap bmp)
    {
        var w = new WriteableBitmap(bmp.Width, bmp.Height);
        var data = bmp.LockBits(new D.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[bmp.Width * 4];
            using var s = w.PixelBuffer.AsStream();
            for (var y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                s.Write(row, 0, row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        w.Invalidate();
        return w;
    }

    // ---------- клавиши и кнопки ----------

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (_text != null) return;
        var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        e.Handled = true;
        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.Z when shift: Redo(); return;
                case VirtualKey.Z: Undo(); return;
                case VirtualKey.Y: Redo(); return;
                case VirtualKey.C: Copy(); return;
                case VirtualKey.S: Save(); return;
            }
            e.Handled = false;
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Enter: Done(); return;
            case VirtualKey.Escape: if (_drawing) Cancel(); else Close(); return;
        }
        foreach (var (tool, _, _, key) in Tools)
            if (e.Key == key) { SelectTool(tool); return; }
        if (e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number5) { _color = _lastColor = e.Key - VirtualKey.Number1; UpdateSwatches(); return; }
        e.Handled = false;
    }

    /// Записать картинку с пометками в файл снимка.
    bool Save()
    {
        if (_text != null) CommitText();
        if (_saved) return true;
        try
        {
            using var bmp = MarkPainter.Render(_source, State);
            using (var mem = new MemoryStream())
            {
                bmp.Save(mem, ImageFormat.Png);
                File.WriteAllBytes(_item.Path, mem.ToArray());
            }
            _saved = true;
            _shots.NoteEdited(_item);
            Log.Write($"редактор: сохранено {_item.Name}");
            return true;
        }
        catch (Exception e)
        {
            Log.Error("редактор: сохранение", e);
            return false;
        }
    }

    void Copy()
    {
        if (_text != null) CommitText();
        using var bmp = MarkPainter.Render(_source, State);
        Clip.SetImage(bmp);
    }

    void Done()
    {
        Save();
        Copy();
        Close();
    }

    async Task SaveAs()
    {
        if (_text != null) CommitText();
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_item.Path) };
            picker.FileTypeChoices.Add("PNG", [".png"]);
            picker.FileTypeChoices.Add("JPEG", [".jpg"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            using var bmp = MarkPainter.Render(_source, State);
            var jpg = file.Path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
            using (var mem = new MemoryStream())
            {
                bmp.Save(mem, jpg ? ImageFormat.Jpeg : ImageFormat.Png);
                await File.WriteAllBytesAsync(file.Path, mem.ToArray());
            }
            Log.Write($"редактор: сохранено как {file.Path}");
        }
        catch (Exception e)
        {
            Log.Error("редактор: сохранить как", e);
        }
    }

    void Close()
    {
        _window.Close();
    }
}
