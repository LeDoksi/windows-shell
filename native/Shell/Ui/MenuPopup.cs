using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Ui;

public sealed class MenuItem
{
    public string Text { get; init; } = "";
    /// Значок Segoe Fluent Icons.
    public string? Glyph { get; init; }
    public bool Danger { get; init; }
    public bool Checked { get; init; }
    public bool Separator { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<MenuItem>? Children { get; init; }
    public Action? Invoke { get; init; }

    public static MenuItem Sep => new() { Separator = true };
}

/// Меню как в Windows 11: стекло, пункты во всю ширину, подменю по наведению через 300 мс,
/// стрелки, Enter, Esc. Своё окно, а не меню браузера.
public sealed class MenuPopup : Popup
{
    const double RowHeight = 34;
    readonly MenuPopup? _parent;
    readonly StackPanel _list = new() { Padding = new Thickness(4) };
    readonly List<(MenuItem Item, Grid Row)> _rows = [];
    readonly DispatcherQueueTimer _submenuTimer;
    readonly DispatcherQueueTimer _graceTimer;
    IReadOnlyList<MenuItem> _items = [];
    int _highlight = -1;
    MenuPopup? _submenu;
    int _submenuOf = -1;
    bool _keyboardInSubmenu;

    public MenuPopup() : this(null) { }

    MenuPopup(MenuPopup? parent) : base(activating: parent is null)
    {
        _parent = parent;
        SetContent(_list);
        var q = DispatcherQueue.GetForCurrentThread();
        _submenuTimer = q.CreateTimer();
        _submenuTimer.Interval = TimeSpan.FromMilliseconds(300);
        _submenuTimer.IsRepeating = false;
        _submenuTimer.Tick += (_, _) => { if (_highlight >= 0) OpenSubmenu(_highlight, keyboard: false); };
        // Курсор ушёл с пункта с подменю наискосок: подменю ждёт 300 мс, вдруг курсор идёт к нему.
        _graceTimer = q.CreateTimer();
        _graceTimer.Interval = TimeSpan.FromMilliseconds(300);
        _graceTimer.IsRepeating = false;
        _graceTimer.Tick += (_, _) =>
        {
            if (_submenu is { IsOpen: true } && _submenu.ContainsCursor()) return;
            // Курсор вне меню (срезал угол по пути к подменю): подменю остаётся открытым.
            if (!ContainsCursor()) { if (_submenu is { IsOpen: true }) SetHighlight(_submenuOf); return; }
            if (_highlight != _submenuOf) CloseSubmenu();
            if (_highlight >= 0 && _items[_highlight].Children != null) OpenSubmenu(_highlight, keyboard: false);
        };
        Win.Root.PointerExited += (_, _) =>
        {
            if (_submenu is { IsOpen: true }) return;
            SetHighlight(-1);
        };
        // Курсор дошёл до подменю: в родительском меню подсвечен пункт, который его открыл.
        Win.Root.PointerEntered += (_, _) =>
        {
            if (_parent is null || _parent._submenuOf < 0) return;
            _parent._graceTimer.Stop();
            _parent.SetHighlight(_parent._submenuOf);
        };
    }

    public void Show(object anchor, Rect anchorPx, IReadOnlyList<MenuItem> items)
    {
        Build(items);
        Open(anchor, anchorPx);
    }

    void Build(IReadOnlyList<MenuItem> items)
    {
        _items = items;
        _rows.Clear();
        _list.Children.Clear();
        _highlight = -1;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Separator)
            {
                _list.Children.Add(new Rectangle
                {
                    Height = 1,
                    Margin = new Thickness(-4, 4, -4, 4),
                    Fill = Theme.Brush(Theme.Separator),
                });
                _rows.Add((item, new Grid()));
                continue;
            }
            var color = !item.Enabled ? Theme.TextTertiary : item.Danger ? Theme.Danger : Theme.Text;
            var row = new Grid
            {
                Height = RowHeight,
                CornerRadius = new CornerRadius(4),
                Background = Theme.Brush(Microsoft.UI.Colors.Transparent),
                Padding = new Thickness(12, 0, 10, 0),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            var glyph = item.Checked ? "" : item.Glyph;
            if (glyph != null)
            {
                row.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontFamily = Theme.Icons,
                    FontSize = 14,
                    Foreground = Theme.Brush(item.Checked ? Theme.Accent : color),
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
            }
            var text = new TextBlock
            {
                Text = item.Text,
                FontFamily = Theme.Font,
                FontSize = 14,
                Foreground = Theme.Brush(color),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 320,
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (item.Children != null)
            {
                var chevron = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 11, Foreground = Theme.Brush(Theme.TextSecondary) };
                Grid.SetColumn(chevron, 2);
                row.Children.Add(chevron);
            }
            var index = i;
            row.PointerEntered += (_, _) => OnHover(index);
            row.PointerPressed += (_, e) => { e.Handled = true; row.Background = Theme.Brush(Theme.Pressed); };
            row.PointerReleased += (_, e) =>
            {
                e.Handled = true;
                var kind = e.GetCurrentPoint(row).Properties.PointerUpdateKind;
                if (kind is Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased or Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased)
                    Activate(index);
            };
            _list.Children.Add(row);
            _rows.Add((item, row));
        }
    }

    protected override Size Measure(double width)
    {
        _list.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _list.DesiredSize;
        return new Size(Math.Clamp(size.Width + 8, 220, 380), size.Height);
    }

    void OnHover(int index)
    {
        if (!_items[index].Enabled) { SetHighlight(index); return; }
        SetHighlight(index);
        _keyboardInSubmenu = false;
        _submenuTimer.Stop();
        if (_submenu is { IsOpen: true })
        {
            if (index == _submenuOf) { _graceTimer.Stop(); return; }
            _graceTimer.Stop();
            _graceTimer.Start();
            return;
        }
        if (_items[index].Children != null) _submenuTimer.Start();
    }

    void SetHighlight(int index)
    {
        _highlight = index;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Item.Separator) continue;
            _rows[i].Row.Background = Theme.Brush(i == index && _rows[i].Item.Enabled ? Theme.Hover : Microsoft.UI.Colors.Transparent);
        }
    }

    void Activate(int index)
    {
        var item = _items[index];
        if (!item.Enabled || item.Separator) return;
        if (item.Children != null) { OpenSubmenu(index, keyboard: true); return; }
        // Действие выполняется, меню закрывается сразу.
        Root.Close(restoreFocus: true);
        item.Invoke?.Invoke();
    }

    MenuPopup Root => _parent?.Root ?? this;

    void OpenSubmenu(int index, bool keyboard)
    {
        if (_submenu is { IsOpen: true } && _submenuOf == index)
        {
            if (keyboard) { _keyboardInSubmenu = true; _submenu.MoveHighlight(+1); }
            return;
        }
        CloseSubmenu();
        var item = _items[index];
        if (item.Children is null || item.Children.Count == 0) return;
        _submenu ??= new MenuPopup(this);
        _submenu.Build(item.Children);
        _submenuOf = index;
        var row = _rows[index].Row;
        var scale = Win.Scale;
        var origin = row.TransformToVisual(null).TransformPoint(new Point(0, 0));
        Native.Win32.GetWindowRect(Win.Hwnd, out var wr);
        var rect = new Rect(wr.Left + origin.X * scale, wr.Top + origin.Y * scale, row.ActualWidth * scale + 4 * scale, row.ActualHeight * scale);
        _submenu.Open(item, rect, beside: true, nested: true);
        if (keyboard) { _keyboardInSubmenu = true; _submenu.MoveHighlight(+1); }
    }

    void CloseSubmenu()
    {
        _graceTimer.Stop();
        _keyboardInSubmenu = false;
        _submenu?.Close(restoreFocus: false);
        _submenuOf = -1;
    }

    void MoveHighlight(int step)
    {
        if (_items.Count == 0) return;
        var i = _highlight;
        for (var n = 0; n < _items.Count; n++)
        {
            i = (i + step + _items.Count) % _items.Count;
            if (i < 0) i = _items.Count - 1;
            if (!_items[i].Separator && _items[i].Enabled) { SetHighlight(i); return; }
        }
    }

    protected override bool HandleKey(VirtualKey key)
    {
        if (_keyboardInSubmenu && _submenu is { IsOpen: true })
        {
            if (key is VirtualKey.Left or VirtualKey.Escape) { CloseSubmenu(); return true; }
            return _submenu.HandleKey(key);
        }
        switch (key)
        {
            case VirtualKey.Down: MoveHighlight(+1); return true;
            case VirtualKey.Up: MoveHighlight(-1); return true;
            case VirtualKey.Enter or VirtualKey.Space:
                if (_highlight >= 0) Activate(_highlight);
                return true;
            case VirtualKey.Right:
                if (_highlight >= 0 && _items[_highlight].Children != null) OpenSubmenu(_highlight, keyboard: true);
                return true;
            case VirtualKey.Left when _parent != null:
                return true;
            case VirtualKey.Escape when _submenu is { IsOpen: true }:
                CloseSubmenu();
                return true;
        }
        return false;
    }

    protected override void OnClosing()
    {
        _submenuTimer.Stop();
        CloseSubmenu();
    }
}
