using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WindowsShell.Island.Core;
using WindowsShell.Ui;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;
using XPath = Microsoft.UI.Xaml.Shapes.Path;

namespace WindowsShell.Island.Widgets;

/// Медиа, вариант B: обложка — кнопка play/pause (значок только при наведении), вокруг кольцо
/// прогресса и перемотки, по бокам предыдущий/следующий. Жест, начатый на кольце, — перемотка
/// до отпускания и никогда не пауза (П19); без длительности кольцо только показывает. Выбор
/// плеера — только когда сессий больше одной (П23).
public sealed class MediaWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    const double Size = 184, R = 84, Cover = 148, Stroke = 6;

    DispatcherQueueTimer? _tick;
    XPath? _arc;
    Ellipse? _thumb, _track;
    TextBlock? _pos, _len, _overlayTime;
    FontIcon? _overlay;
    Border? _overlayBox;
    bool _seeking, _suppressClick, _pickOpen;
    double _seekFraction;
    string _shownKey = "";

    public override string Id => "media";
    public override double Width => 500;

    protected override void OnOpen()
    {
        S.Media.Changed += OnMedia;
        S.Media.TimelineChanged += UpdateRing;
        _tick ??= S.Queue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(500);
        _tick.Tick -= OnTick;
        _tick.Tick += OnTick;
        _tick.Start();
    }

    protected override void OnClose()
    {
        S.Media.Changed -= OnMedia;
        S.Media.TimelineChanged -= UpdateRing;
        _tick?.Stop();
        _seeking = false;
    }

    void OnTick(DispatcherQueueTimer t, object o) { if (S.Media.Timeline?.Playing == true) UpdateRing(); }

    void OnMedia()
    {
        var m = S.Media.Current;
        var key = $"{m?.AppId}|{m?.Title}|{m?.Artist}|{m?.Playing}|{S.Media.Cover?.GetHashCode()}|{S.Media.Sessions.Count}";
        if (key != _shownKey) Rebuild();
    }

    protected override void Build()
    {
        var media = S.Media;
        var m = media.Current;
        _shownKey = $"{m?.AppId}|{m?.Title}|{m?.Artist}|{m?.Playing}|{media.Cover?.GetHashCode()}|{media.Sessions.Count}";
        if (m is null)
        {
            var empty = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 18) };
            var icon = Glyph("", 34, Faint);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(icon);
            var t = Text("Сейчас ничего не играет", 15, Sub);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(t);
            var hint = Text("Включи музыку или видео — здесь появятся обложка и управление.", 12, Faint, wrap: true);
            hint.TextAlignment = TextAlignment.Center;
            empty.Children.Add(hint);
            Root.Children.Add(empty);
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 34, HorizontalAlignment = HorizontalAlignment.Center };
        var prev = Round("", 52, "Предыдущий трек", out _);
        prev.IsEnabled = m.CanPrevious;
        prev.Click += (_, _) => media.Previous();
        var next = Round("", 52, "Следующий трек", out _);
        next.IsEnabled = m.CanNext;
        next.Click += (_, _) => media.Next();
        row.Children.Add(prev);
        row.Children.Add(BuildRing(m.Playing, m.CanPlayPause));
        row.Children.Add(next);
        Root.Children.Add(row);

        var times = new Grid { Width = 250, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -6, 0, 0) };
        _pos = Text("", 12, Sub);
        _len = Text("", 12, Sub);
        _len.HorizontalAlignment = HorizontalAlignment.Right;
        times.Children.Add(_pos);
        times.Children.Add(_len);
        Root.Children.Add(times);

        var title = Text(m.Title.Length > 0 ? m.Title : "Без названия", 19, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.MaxWidth = 440;
        ToolTipService.SetToolTip(title, m.Title);
        Root.Children.Add(title);
        if (m.Artist.Length > 0)
        {
            var artist = Text(m.Artist, 14, Sub);
            artist.HorizontalAlignment = HorizontalAlignment.Center;
            artist.MaxWidth = 440;
            artist.Margin = new Thickness(0, -8, 0, 0);
            Root.Children.Add(artist);
        }
        Root.Children.Add(Source(m.AppId));
        UpdateRing();
    }

    /// «Играет в «…»»; при нескольких плеерах — переключатель (П23).
    UIElement Source(string appId)
    {
        var name = S.Media.Sessions.FirstOrDefault(x => x.AppId == appId)?.Name ?? WindowsShell.Media.MediaService.SourceName(appId);
        var many = S.Media.Sessions.Count > 1;
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        chip.Children.Add(Glyph("", 14, Sub));
        chip.Children.Add(Text($"Играет в «{name}»", 13, Color.FromArgb(255, 0xD9, 0xD9, 0xDE)));
        if (many) chip.Children.Add(Glyph(_pickOpen ? "" : "", 10, Sub));
        var button = new Button
        {
            Content = chip,
            Background = B(Group),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = many,
            IsTabStop = many,
        };
        AutomationProperties.SetName(button, many ? $"Плеер: {name}. Выбрать другой" : $"Играет в {name}");
        button.Click += (_, _) => { _pickOpen = !_pickOpen; Rebuild(); };
        if (!many || !_pickOpen) return button;
        var list = new StackPanel { Spacing = 2 };
        foreach (var src in S.Media.Sessions)
        {
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(Text(src.Name, 13));
            if (src.AppId == appId)
            {
                var ok = Glyph("", 12);
                Grid.SetColumn(ok, 1);
                content.Children.Add(ok);
            }
            var r = Row(content, on: src.AppId == appId, name: src.Name);
            var id = src.AppId;
            r.Click += (_, _) => { _pickOpen = false; S.Media.Select(id); };
            list.Children.Add(r);
        }
        return new StackPanel { Spacing = 8, Width = 300, HorizontalAlignment = HorizontalAlignment.Center, Children = { button, GroupBox(list) } };
    }

    FrameworkElement BuildRing(bool playing, bool canPlay)
    {
        var grid = new Grid { Width = Size, Height = Size };
        _track = new Ellipse { Width = 2 * R, Height = 2 * R, Stroke = B(Track), StrokeThickness = Stroke, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(_track);
        _arc = new XPath { Stroke = B(Ink), StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        grid.Children.Add(_arc);
        _thumb = new Ellipse { Width = 14, Height = 14, Fill = B(Ink), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        grid.Children.Add(_thumb);

        var cover = new Grid { Width = Cover, Height = Cover, CornerRadius = new CornerRadius(Cover / 2), Background = B(Color.FromArgb(255, 0x1C, 0x1C, 0x1F)) };
        if (S.Media.Cover is { } img)
            cover.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill, Width = Cover, Height = Cover });
        else
        {
            var note = Glyph("", 36, Faint);
            note.HorizontalAlignment = HorizontalAlignment.Center;
            cover.Children.Add(note);
        }
        _overlay = Glyph(playing ? "" : "", 34, Microsoft.UI.Colors.White);
        _overlay.HorizontalAlignment = HorizontalAlignment.Center;
        _overlayTime = Text("", 18, Microsoft.UI.Colors.White, bold: true);
        _overlayTime.HorizontalAlignment = HorizontalAlignment.Center;
        _overlayTime.Visibility = Visibility.Collapsed;
        _overlayBox = new Border { Background = B(Color.FromArgb(0x73, 0, 0, 0)), Opacity = 0, Child = new Grid { Children = { _overlay, _overlayTime } } };
        cover.Children.Add(_overlayBox);
        cover.HorizontalAlignment = HorizontalAlignment.Center;
        cover.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(cover);

        // Одна кнопка на всё кольцо: Enter/пробел и диктор — пауза, стрелки — перемотка на 10 с.
        var button = new Button
        {
            Content = grid,
            Width = Size,
            Height = Size,
            Padding = new Thickness(0),
            Background = B(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Size / 2),
            IsEnabled = true,
        };
        AutomationProperties.SetName(button, playing ? "Пауза" : "Играть");
        button.Click += (_, _) =>
        {
            if (_suppressClick) { _suppressClick = false; return; }
            if (canPlay) S.Media.PlayPause();
        };
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var p = e.GetCurrentPoint(button).Position;
            var dx = p.X - Size / 2;
            var dy = p.Y - Size / 2;
            _suppressClick = false;
            if (!MediaRing.OnRing(dx, dy, Cover / 2, Size / 2 + 4)) return;
            // Жест с кольца — только перемотка; без перемотки — ничего (без ложной паузы).
            _suppressClick = true;
            if (!Seekable()) return;
            _seeking = true;
            _seekFraction = MediaRing.Fraction(dx, dy);
            button.CapturePointer(e.Pointer);
            UpdateRing();
        }), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            var p = e.GetCurrentPoint(button).Position;
            var dx = p.X - Size / 2;
            var dy = p.Y - Size / 2;
            if (_seeking) { _seekFraction = MediaRing.Fraction(dx, dy); UpdateRing(); return; }
            // Значок play/pause — только пока курсор на обложке.
            if (_overlayBox != null) _overlayBox.Opacity = Math.Sqrt(dx * dx + dy * dy) < Cover / 2 ? 1 : 0;
        }), true);
        button.PointerExited += (_, _) => { if (!_seeking && _overlayBox != null) _overlayBox.Opacity = 0; };
        void End(bool apply)
        {
            if (!_seeking) return;
            _seeking = false;
            if (apply && S.Media.Timeline is { } t) S.Media.Seek(TimeSpan.FromSeconds(t.Duration.TotalSeconds * _seekFraction));
            UpdateRing();
        }
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => { End(true); button.ReleasePointerCaptures(); }), true);
        button.PointerCaptureLost += (_, _) => End(true);
        button.KeyDown += (_, e) =>
        {
            if (e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right) || !Seekable() || S.Media.Timeline is not { } t) return;
            var by = e.Key == Windows.System.VirtualKey.Left ? -10 : 10;
            S.Media.Seek(t.Now + TimeSpan.FromSeconds(by));
            e.Handled = true;
        };
        return button;
    }

    bool Seekable() => S.Media.Timeline is { } t && MediaRing.Seekable(t.CanSeek, t.Duration);

    void UpdateRing()
    {
        if (_arc is null || _pos is null || _len is null) return;
        var t = S.Media.Timeline;
        var seekable = Seekable();
        var known = t is { } tl && tl.Duration.TotalSeconds > 1;
        var fraction = _seeking ? _seekFraction : known ? Math.Clamp(t!.Now.TotalSeconds / t.Duration.TotalSeconds, 0, 1) : 0;
        _arc.Stroke = B(_seeking ? Theme.Accent : Ink);
        _arc.StrokeThickness = _seeking ? Stroke + 1 : Stroke;
        _track!.StrokeThickness = known ? Stroke : 3;
        _arc.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        _thumb!.Visibility = seekable ? Visibility.Visible : Visibility.Collapsed;
        _arc.Data = Arc(fraction);
        var a = fraction * 2 * Math.PI;
        var tx = Size / 2 + R * Math.Sin(a);
        var ty = Size / 2 - R * Math.Cos(a);
        _thumb.Margin = new Thickness(tx - 7, ty - 7, 0, 0);
        _pos.Text = known ? MediaRing.Time(_seeking ? TimeSpan.FromSeconds(t!.Duration.TotalSeconds * fraction) : t!.Now) : "";
        _len.Text = known ? MediaRing.Time(t!.Duration) : "";
        if (_overlay != null && _overlayTime != null && _overlayBox != null)
        {
            _overlay.Visibility = _seeking ? Visibility.Collapsed : Visibility.Visible;
            _overlayTime.Visibility = _seeking ? Visibility.Visible : Visibility.Collapsed;
            if (_seeking) { _overlayTime.Text = _pos.Text; _overlayBox.Opacity = 1; }
        }
    }

    static Geometry Arc(double fraction)
    {
        var c = Size / 2;
        if (fraction >= 0.999) return new EllipseGeometry { Center = new Windows.Foundation.Point(c, c), RadiusX = R, RadiusY = R };
        var a = Math.Max(0.0005, fraction) * 2 * Math.PI;
        var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(c, c - R), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = new Windows.Foundation.Point(c + R * Math.Sin(a), c - R * Math.Cos(a)),
            Size = new Windows.Foundation.Size(R, R),
            IsLargeArc = fraction > 0.5,
            SweepDirection = SweepDirection.Clockwise,
        });
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }
}
