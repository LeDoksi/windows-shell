using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WindowsShell.Ui;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Panel;

/// Поле для названия категории (новая — по ＋, переименовать — из меню): Enter сохраняет,
/// Esc отменяет, клик мимо тоже отменяет.
public sealed class NameFlyout : Popup
{
    readonly TextBox _box;
    readonly TextBlock _title;
    Action<string>? _done;

    public NameFlyout() : base(activating: true)
    {
        _title = new TextBlock { FontFamily = Theme.Font, FontSize = 12, Foreground = Theme.Brush(Theme.TextSecondary), Margin = new Thickness(2, 0, 0, 6) };
        _box = new TextBox { FontFamily = Theme.Font, FontSize = 14, MaxLength = 40, MinWidth = 240 };
        _box.KeyDown += OnKey;
        var hint = new TextBlock
        {
            Text = "Enter — сохранить, Esc — отмена",
            FontFamily = Theme.Font,
            FontSize = 11,
            Foreground = Theme.Brush(Theme.TextTertiary),
            Margin = new Thickness(2, 6, 0, 0),
        };
        SetContent(new StackPanel { Padding = new Thickness(12, 10, 12, 10), Children = { _title, _box, hint } });
    }

    protected override double Gap => 8;

    public void Show(object anchor, Rect anchorPx, string title, string text, object? owner, Action<string> done)
    {
        _title.Text = title;
        _box.Text = text;
        _done = done;
        Open(anchor, anchorPx, Placement.Below, owner: owner);
        _box.Focus(FocusState.Programmatic);
        _box.SelectAll();
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var name = _box.Text.Trim();
            var done = _done;
            _done = null;
            Close(restoreFocus: true);
            // Пустое название не принимаем: у категории на панели должна быть подпись.
            if (name.Length > 0) done?.Invoke(name);
        }
    }
}
