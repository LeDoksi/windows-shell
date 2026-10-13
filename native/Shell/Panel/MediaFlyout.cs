using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WindowsShell.Media;
using WindowsShell.Ui;

namespace WindowsShell.Panel;

/// Окошко «Сейчас играет» (раздел 4): обложка, название, исполнитель и кнопки ⏮ ⏯ ⏭.
/// Музыка закончилась — окошко закрывается само.
public sealed class MediaFlyout : Popup
{
    readonly MediaService _media;
    readonly Image _cover = new() { Width = 88, Height = 88, Stretch = Stretch.UniformToFill };
    readonly FontIcon _coverEmpty = new() { Glyph = "", FontFamily = Theme.Icons, FontSize = 32, Foreground = Theme.Brush(Theme.TextSecondary) };
    readonly TextBlock _title = new() { FontFamily = Theme.Font, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Theme.Brush(Theme.Text), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _artist = new() { FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.TextSecondary), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button _prev, _play, _next;
    readonly FontIcon _playIcon;

    public MediaFlyout(MediaService media) : base(activating: true)
    {
        _media = media;
        _prev = Btn("", out _);
        _play = Btn("", out _playIcon);
        _next = Btn("", out _);
        _prev.Click += (_, _) => _media.Previous();
        _play.Click += (_, _) => _media.PlayPause();
        _next.Click += (_, _) => _media.Next();

        var coverBox = new Grid
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(8),
            Background = Theme.Brush(Theme.Card),
            Children = { _coverEmpty, _cover },
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _artist } };
        var top = new Grid { ColumnSpacing = 14 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(coverBox);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), Children = { _prev, _play, _next } };
        SetContent(new StackPanel { Padding = new Thickness(16, 16, 16, 12), Children = { top, buttons } });
        _media.Changed += () =>
        {
            if (!IsOpen) return;
            if (_media.Current is null) { Close(restoreFocus: true); return; }
            Refresh();
        };
    }

    protected override double Width => 340;
    protected override double Gap => 8;

    static Button Btn(string glyph, out FontIcon icon)
    {
        icon = new FontIcon { Glyph = glyph, FontFamily = Theme.Icons, FontSize = 16 };
        return new Button
        {
            Content = icon,
            Width = 48,
            Height = 40,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(Theme.Card),
            BorderThickness = new Thickness(0),
        };
    }

    public void Show(object anchor, Rect anchorPx, object owner)
    {
        Refresh();
        Open(anchor, anchorPx, Placement.Below, owner: owner);
    }

    void Refresh()
    {
        var m = _media.Current;
        _title.Text = m?.Title is { Length: > 0 } t ? t : "Без названия";
        _artist.Text = m?.Artist ?? "";
        _artist.Visibility = string.IsNullOrEmpty(m?.Artist) ? Visibility.Collapsed : Visibility.Visible;
        _cover.Source = _media.Cover;
        _coverEmpty.Visibility = _media.Cover is null ? Visibility.Visible : Visibility.Collapsed;
        _playIcon.Glyph = m?.Playing == true ? "" : "";
        _play.IsEnabled = m?.CanPlayPause ?? false;
        _next.IsEnabled = m?.CanNext ?? false;
        _prev.IsEnabled = m?.CanPrevious ?? false;
    }
}
