using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Dock;

/// Список окон приложения с живыми миниатюрами DWM (раздел 1.3). Не активируется: появляется
/// по наведению и не забирает фокус у окна, в котором работаешь.
public sealed class WindowListPopup : Popup
{
    const double Pad = 8, CardW = 220, CardH = 164, ThumbW = 204, ThumbH = 120, ThumbTop = 36, CardGap = 8;

    readonly WindowTracker _tracker;
    readonly Icons _icons;
    readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Spacing = CardGap, Padding = new Thickness(Pad) };
    readonly List<IntPtr> _thumbs = [];
    IReadOnlyList<WindowInfo> _windows = [];

    public WindowListPopup(WindowTracker tracker, Icons icons) : base(activating: false)
    {
        _tracker = tracker;
        _icons = icons;
        SetContent(_cards);
        Win.Root.PointerExited += (_, _) => PointerLeft?.Invoke();
    }

    /// Курсор ушёл со списка.
    public event Action? PointerLeft;

    protected override double Gap => 14;

    public IReadOnlyList<WindowInfo> Windows => _windows;

    public void Show(object anchor, Rect anchorPx, IReadOnlyList<WindowInfo> windows, string iconSource)
    {
        Build(windows, iconSource);
        Open(anchor, anchorPx);
    }

    protected override Size Measure(double width) =>
        new(Pad * 2 + _windows.Count * CardW + Math.Max(0, _windows.Count - 1) * CardGap, Pad * 2 + CardH);

    void Build(IReadOnlyList<WindowInfo> windows, string iconSource)
    {
        _windows = windows.Take(8).ToList();
        _cards.Children.Clear();
        foreach (var w in _windows)
        {
            var card = new Grid
            {
                Width = CardW,
                Height = CardH,
                CornerRadius = new CornerRadius(6),
                Background = Theme.Brush(Theme.Card),
                Padding = new Thickness(8, 6, 6, 8),
            };
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            var icon = new Image { Width = 16, Height = 16 };
            _icons.Get(iconSource, (int)Math.Ceiling(16 * Win.Scale), bmp => icon.Source = bmp);
            head.Children.Add(icon);
            var title = new TextBlock
            {
                Text = w.Title,
                FontFamily = Theme.Font,
                FontSize = 12,
                Foreground = Theme.Brush(Theme.Text),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 1);
            head.Children.Add(title);
            var close = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(4),
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                Child = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 10, Foreground = Theme.Brush(Theme.TextSecondary) },
                Opacity = 0,
            };
            Grid.SetColumn(close, 2);
            head.Children.Add(close);
            card.Children.Add(head);

            var hwnd = w.Hwnd;
            card.PointerEntered += (_, _) => { card.Background = Theme.Brush(Theme.Hover); close.Opacity = 1; };
            card.PointerExited += (_, _) => { card.Background = Theme.Brush(Theme.Card); close.Opacity = 0; };
            close.PointerEntered += (_, _) => close.Background = Theme.Brush(global::Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
            close.PointerExited += (_, _) => close.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
            close.PointerReleased += (_, e) => { e.Handled = true; _tracker.Close(hwnd); };
            card.PointerReleased += (_, e) =>
            {
                e.Handled = true;
                var kind = e.GetCurrentPoint(card).Properties.PointerUpdateKind;
                if (kind == PointerUpdateKind.MiddleButtonReleased) { _tracker.Close(hwnd); return; }
                if (kind != PointerUpdateKind.LeftButtonReleased) return;
                Close(restoreFocus: false);
                _tracker.Activate(hwnd);
            };
            _cards.Children.Add(card);
        }
    }

    protected override void OnOpened() => UpdateThumbnails();

    /// Миниатюры рисует DWM прямо в наше окно, поверх карточек: живые и без копирования картинок.
    void UpdateThumbnails()
    {
        ClearThumbnails();
        var scale = Win.Scale;
        for (var i = 0; i < _windows.Count; i++)
        {
            if (DwmRegisterThumbnail(Win.Hwnd, _windows[i].Hwnd, out var thumb) != 0) continue;
            _thumbs.Add(thumb);
            DwmQueryThumbnailSourceSize(thumb, out var src);
            double sw = Math.Max(1, src.cx), sh = Math.Max(1, src.cy);
            var fit = Math.Min(ThumbW / sw, ThumbH / sh);
            double tw = sw * fit, th = sh * fit;
            var left = Pad + i * (CardW + CardGap) + 8 + (ThumbW - tw) / 2;
            var top = Pad + ThumbTop + (ThumbH - th) / 2;
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT
                {
                    Left = (int)(left * scale),
                    Top = (int)(top * scale),
                    Right = (int)((left + tw) * scale),
                    Bottom = (int)((top + th) * scale),
                },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    void ClearThumbnails()
    {
        foreach (var t in _thumbs) DwmUnregisterThumbnail(t);
        _thumbs.Clear();
    }

    protected override void OnClosing() => ClearThumbnails();
}
