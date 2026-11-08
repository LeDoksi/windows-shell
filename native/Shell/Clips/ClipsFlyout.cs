using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using static WindowsShell.Tail.FlyoutUi;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Clips;

/// Окошко Win+V (design-v2 п. 5): у текстового курсора (нет его — у мыши), сверху поиск
/// (печатаешь — ищет), ниже закреплённые и история. Клик или Enter — вставить в окно, где был
/// курсор; Shift — без оформления; Ctrl+1…9 — пункт по номеру. Наведение — «Закрепить» и
/// «Удалить»; закреплённые меняются местами перетаскиванием. У текста — «Аа» (Ctrl+T):
/// преобразования (раскладка, регистр, переносы) с просмотром «было → стало» (WSH-E26).
public sealed class ClipsFlyout : Popup
{
    const double W = 400, ListMax = 460, ThumbH = 96;
    const int Quick = 9;

    readonly ClipsService _clips;
    readonly MenuPopup _menu = new();
    readonly TextBox _search;
    readonly StackPanel _list = new() { Spacing = 2 };
    readonly ScrollViewer _scroll;
    readonly StackPanel _root;
    readonly Grid _searchRow;
    readonly TextBlock _hint;
    readonly StackPanel _fixPanel = new() { Spacing = 8, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 4, 0) };
    ClipEntry? _fix;
    string _fixText = "";
    List<(string Id, string Title, string Result)> _fixOptions = [];
    int _fixSel;
    readonly List<Button> _fixChips = [];
    const string Hint = "Enter — вставить · Shift+Enter — без оформления · Ctrl+1…9";
    readonly Dictionary<string, ImageSource?> _thumbs = [];
    readonly List<RowView> _rows = [];
    int _sel;
    IntPtr _target;

    // Перетаскивание закреплённого.
    RowView? _press;
    double _pressY;
    bool _dragging;
    int _dragTo;

    sealed class RowView
    {
        public required ClipEntry Entry { get; init; }
        public required Grid View { get; init; }
        public required StackPanel Actions { get; init; }
        public required TextBlock Number { get; init; }
        public TranslateTransform Shift { get; } = new();
        public bool Hover;
    }

    public ClipsFlyout(ClipsService clips) : base(activating: true)
    {
        _clips = clips;
        _search = new TextBox
        {
            PlaceholderText = "Поиск в буфере обмена",
            FontFamily = Theme.Font,
            FontSize = 14,
            Height = 34,
            Padding = new Thickness(34, 6, 10, 0),
            CornerRadius = new CornerRadius(17),
        };
        _search.TextChanged += (_, _) => { _sel = 0; Build(); Layout(); };
        _search.PreviewKeyDown += OnKey;
        var searchIcon = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 13, Foreground = Theme.Brush(Theme.TextSecondary), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(13, 0, 0, 0), IsHitTestVisible = false };
        _scroll = new ScrollViewer
        {
            Content = _list,
            MaxHeight = ListMax,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0),
        };
        var hint = _hint = new TextBlock
        {
            Text = Hint,
            FontFamily = Theme.Font, FontSize = 11, Foreground = Theme.Brush(Theme.TextTertiary),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 0),
        };
        _root = new StackPanel
        {
            Padding = new Thickness(10, 10, 6, 10),
            Spacing = 8,
            Children = { (_searchRow = new Grid { Margin = new Thickness(0, 0, 4, 0), Children = { _search, searchIcon } }), _scroll, _fixPanel, hint },
        };
        _fixPanel.PreviewKeyDown += OnFixKey;
        SetContent(_root);
        _clips.Changed += () =>
        {
            if (!IsOpen || _dragging) return;
            Build();
            Layout();
        };
    }

    protected override double Width => W;
    protected override double Gap => 6;

    protected override Size Measure(double width)
    {
        _root.Measure(new Size(width, double.PositiveInfinity));
        return _root.DesiredSize;
    }

    /// Win+V: открыть у курсора окна, где печатали; открыто — закрыть.
    public void Toggle(IntPtr target)
    {
        if (IsOpen) { Close(restoreFocus: true); return; }
        _target = target;
        EndFix(focus: false);
        var caret = target != IntPtr.Zero ? Caret.Find(target) : null;
        _search.Text = "";
        _sel = 0;
        _press = null;
        _dragging = false;
        Build();
        if (caret is { } c) Open("clips", c, Placement.Caret);
        else
        {
            GetCursorPos(out var p);
            Open("clips", new Rect(p.X, p.Y, 1, 1), Placement.AtPoint);
        }
        _search.Focus(FocusState.Programmatic);
        _scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    // ---------- список ----------

    List<ClipEntry> Filter(IEnumerable<ClipEntry> list)
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) return list.ToList();
        return list.Where(e =>
            (e.Text?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (e.Files?.Any(f => f.Contains(q, StringComparison.CurrentCultureIgnoreCase)) ?? false)
            || (e.Source?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (e.Kind == ClipKind.Image && "картинка".Contains(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    void Build()
    {
        _list.Children.Clear();
        _rows.Clear();
        var pinned = Filter(_clips.Store.Pinned);
        var history = Filter(_clips.Store.History);
        if (pinned.Count > 0)
        {
            _list.Children.Add(Header("Закреплённые"));
            foreach (var e in pinned) AddRow(e);
        }
        if (history.Count > 0)
        {
            if (pinned.Count > 0) _list.Children.Add(Header("Недавние"));
            foreach (var e in history) AddRow(e);
        }
        if (_rows.Count == 0)
            _list.Children.Add(new TextBlock
            {
                Text = _search.Text.Trim().Length > 0 ? "Ничего не нашлось" : "Здесь появится всё, что скопируешь: текст, картинки, файлы. Хранится 3 дня, закреплённое — всегда.",
                FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.TextSecondary),
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                Margin = new Thickness(16, 18, 16, 18),
            });
        _sel = Math.Clamp(_sel, 0, Math.Max(0, _rows.Count - 1));
        Paint();
    }

    static TextBlock Header(string text) => new()
    {
        Text = text, FontFamily = Theme.Font, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Theme.Brush(Theme.TextSecondary), Margin = new Thickness(8, 6, 0, 2),
    };

    void AddRow(ClipEntry e)
    {
        var index = _rows.Count;
        var content = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(Body(e));
        // «Картинка 1920 × 1080 · 5 мин назад · Google Chrome»; у закреплённого время не важно.
        var meta = new List<string>();
        if (e.Kind == ClipKind.Image && e.Width > 0) meta.Add($"Картинка {e.Width} × {e.Height}");
        if (!e.Pinned) meta.Add(Tail.BinFlyout.When(e.Time));
        if (e.Source != null) meta.Add(e.Source);
        if (meta.Count > 0) content.Children.Add(Label(string.Join(" · ", meta), 11, secondary: true));

        var number = new TextBlock
        {
            Text = index < Quick ? (index + 1).ToString() : "",
            FontFamily = Theme.Font, FontSize = 11, Foreground = Theme.Brush(Theme.TextTertiary),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 4, 0),
        };
        ToolTipService.SetToolTip(number, index < Quick ? $"Ctrl+{index + 1}" : null);
        var pin = Mini(e.Pinned ? "" : "", e.Pinned ? "Открепить" : "Закрепить", () => _clips.SetPinned(e, !e.Pinned));
        var fix = e.Kind == ClipKind.Text ? MiniText("Аа", "Преобразовать (Ctrl+T): раскладка, регистр, переносы", () => StartFix(e)) : null;
        var del = Mini("", "Удалить", () => _clips.Remove(e));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Children = { pin, del } };
        if (fix != null) actions.Children.Insert(0, fix);
        var right = new Grid { Children = { number, actions } };

        var view = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 7, 6, 7), CornerRadius = new CornerRadius(6), Background = Theme.Brush(Microsoft.UI.Colors.Transparent) };
        view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        view.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        view.Children.Add(content);
        Grid.SetColumn(right, 1);
        view.Children.Add(right);
        var row = new RowView { Entry = e, View = view, Actions = actions, Number = number };
        view.RenderTransform = row.Shift;
        if (e.Kind == ClipKind.Text) ToolTipService.SetToolTip(content, Tooltip(e.Text));

        view.PointerEntered += (_, _) => { row.Hover = true; Paint(); };
        view.PointerExited += (_, _) => { row.Hover = false; Paint(); };
        view.PointerPressed += (_, a) => OnPressed(row, a);
        view.PointerMoved += (_, a) => OnMoved(row, a);
        view.PointerReleased += (_, a) => OnReleased(row, a);
        view.PointerCaptureLost += (_, _) => { if (_press == row) CancelDrag(); };
        view.RightTapped += (_, a) => { a.Handled = true; ShowMenu(row, a.GetPosition(null)); };
        _rows.Add(row);
        _list.Children.Add(view);
    }

    UIElement Body(ClipEntry e)
    {
        switch (e.Kind)
        {
            case ClipKind.Image:
            {
                var image = new Image { Height = ThumbH, MaxWidth = W - 80, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                LoadThumb(e, image);
                return new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Child = image };
            }
            case ClipKind.Files:
            {
                var files = e.Files ?? [];
                var first = files.FirstOrDefault() ?? "";
                var folder = Directory.Exists(first);
                var name = files.Count == 1 ? Path.GetFileName(first.TrimEnd('\\')) is { Length: > 0 } n ? n : first
                    : $"{Objects(files.Count)}: {string.Join(", ", files.Take(3).Select(f => Path.GetFileName(f.TrimEnd('\\'))))}{(files.Count > 3 ? "…" : "")}";
                var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(Label(name, 13));
                text.Children.Add(Label(Path.GetDirectoryName(first.TrimEnd('\\')) ?? "", 11, secondary: true));
                var g = new Grid { ColumnSpacing = 10 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var icon = Icon(files.Count > 1 ? "" : folder ? "" : "", 18);
                icon.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(icon);
                Grid.SetColumn(text, 1);
                g.Children.Add(text);
                return g;
            }
            default:
                return new TextBlock
                {
                    Text = Preview(e.Text ?? ""),
                    FontFamily = Theme.Font, FontSize = 13, Foreground = Theme.Brush(Theme.Text),
                    TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis,
                };
        }
    }

    /// Начало текста для строки: без пустых строк в начале, табуляции — пробелы.
    static string Preview(string text)
    {
        var t = text.Length > 600 ? text[..600] : text;
        t = t.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Trim('\n', ' ');
        return t.Length == 0 ? "(пробелы)" : t;
    }

    static string? Tooltip(string? text) => text is null || text.Length < 120 ? null : text.Length > 1500 ? text[..1500] + "…" : text;

    static string Objects(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => $"{n} файл",
        (2 or 3 or 4, not (12 or 13 or 14)) => $"{n} файла",
        _ => $"{n} файлов",
    };

    Button Mini(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = Icon(glyph, 13),
            Width = 28, Height = 26, Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = Theme.Brush(Theme.Card),
            BorderThickness = new Thickness(0),
            IsTabStop = false,
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    Button MiniText(string text, string tip, Action click)
    {
        var b = Mini("", tip, click);
        b.Content = Label(text, 12, bold: true);
        b.Width = 32;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    // ---------- преобразования (WSH-E26) ----------

    /// Панель «Преобразовать» вместо списка: варианты, «было → стало», «Вставить» / «Только скопировать».
    void StartFix(ClipEntry e)
    {
        if (e.Kind != ClipKind.Text) return;
        string text;
        try { text = _clips.Store.ReadText(e); }
        catch (Exception ex) { Log.Error("буфер обмена: прочитать текст", ex); return; }
        _fix = e;
        _fixText = text;
        _fixOptions = TextFix.Options(text);
        var suggest = TextFix.Suggest(text);
        _fixSel = Math.Max(0, _fixOptions.FindIndex(o => o.Id == suggest));
        _searchRow.Visibility = Visibility.Collapsed;
        _scroll.Visibility = Visibility.Collapsed;
        _fixPanel.Visibility = Visibility.Visible;
        _hint.Text = "←/→ — вариант · Enter — вставить · Shift+Enter — только скопировать · Esc — назад";
        BuildFix();
        Layout();
        FocusChip();
        Log.Write($"буфер обмена: преобразование — вариантов {_fixOptions.Count}, подсказка {suggest ?? "нет"}");
    }

    void EndFix(bool focus)
    {
        if (_fix is null) return;
        _fix = null;
        _fixText = "";
        _fixOptions = [];
        _fixChips.Clear();
        _fixPanel.Children.Clear();
        _fixPanel.Visibility = Visibility.Collapsed;
        _searchRow.Visibility = Visibility.Visible;
        _scroll.Visibility = Visibility.Visible;
        _hint.Text = Hint;
        if (!focus) return;
        Layout();
        _search.Focus(FocusState.Programmatic);
    }

    static readonly Windows.UI.Color OnAccent = Windows.UI.Color.FromArgb(255, 0x05, 0x14, 0x1F);

    void BuildFix()
    {
        _fixPanel.Children.Clear();
        _fixChips.Clear();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Label("Преобразовать", 14, bold: true));
        var back = new Button { Content = Label("Назад", 12), CornerRadius = new CornerRadius(5), Background = Theme.Brush(Theme.Card), BorderThickness = new Thickness(0), Padding = new Thickness(10, 3, 10, 4) };
        ToolTipService.SetToolTip(back, "Esc");
        back.Click += (_, _) => EndFix(focus: true);
        Grid.SetColumn(back, 1);
        head.Children.Add(back);
        _fixPanel.Children.Add(head);
        if (_fixOptions.Count == 0)
        {
            var none = Label("Тут нечего менять: раскладка, регистр и переносы уже в порядке.", 13, secondary: true);
            none.TextWrapping = TextWrapping.Wrap;
            _fixPanel.Children.Add(none);
            return;
        }
        var chips = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemHeight = 34 };
        for (var i = 0; i < _fixOptions.Count; i++)
        {
            var index = i;
            var on = i == _fixSel;
            var label = Label(_fixOptions[i].Title, 12, bold: on);
            if (on) label.Foreground = Theme.Brush(OnAccent);
            var chip = new Button
            {
                Content = label,
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(14),
                Background = Theme.Brush(on ? Theme.Accent : Theme.Card),
                BorderThickness = new Thickness(0),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"{_fixOptions[i].Title}{(on ? ", выбрано" : "")}");
            chip.Click += (_, _) => { _fixSel = index; BuildFix(); Layout(); FocusChip(); };
            _fixChips.Add(chip);
            chips.Children.Add(chip);
        }
        _fixPanel.Children.Add(chips);
        _fixPanel.Children.Add(Box("Было", _fixText, 4, secondary: true));
        _fixPanel.Children.Add(Box("Стало", _fixOptions[_fixSel].Result, 6, secondary: false));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = Label("Только скопировать", 13), CornerRadius = new CornerRadius(6), Background = Theme.Brush(Theme.Card), BorderThickness = new Thickness(0), Padding = new Thickness(12, 6, 12, 6) };
        copy.Click += (_, _) => ApplyFix(paste: false);
        var pasteLabel = Label("Вставить", 13, bold: true);
        pasteLabel.Foreground = Theme.Brush(OnAccent);
        var paste = new Button { Content = pasteLabel, CornerRadius = new CornerRadius(6), Background = Theme.Brush(Theme.Accent), BorderThickness = new Thickness(0), Padding = new Thickness(14, 6, 14, 6) };
        paste.Click += (_, _) => ApplyFix(paste: true);
        buttons.Children.Add(copy);
        buttons.Children.Add(paste);
        _fixPanel.Children.Add(buttons);
    }

    static UIElement Box(string title, string text, int lines, bool secondary)
    {
        var t = new TextBlock
        {
            Text = Preview(text), FontFamily = Theme.Font, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = lines,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Theme.Brush(secondary ? Theme.TextSecondary : Theme.Text),
        };
        var stack = new StackPanel { Spacing = 3, Children = { Label(title, 11, secondary: true), t } };
        return new Border { Background = Theme.Brush(Theme.Card), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 8), Child = stack };
    }

    void FocusChip()
    {
        if (_fixChips.Count > 0) _fixChips[Math.Clamp(_fixSel, 0, _fixChips.Count - 1)].Focus(FocusState.Programmatic);
        else _fixPanel.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Programmatic);
    }

    void ApplyFix(bool paste)
    {
        if (_fix is null || _fixOptions.Count == 0) return;
        var (_, title, result) = _fixOptions[_fixSel];
        var target = _target;
        if (paste && target != IntPtr.Zero && IsWindow(target)) SetForegroundWindow(target);
        EndFix(focus: false);
        Close(restoreFocus: !paste);
        _clips.PutTransformed(result, title, paste, target);
    }

    void OnFixKey(object sender, KeyRoutedEventArgs e)
    {
        if (_fix is null) return;
        var shift = (GetKeyState(0x10) & 0x8000) != 0;
        switch (e.Key)
        {
            case VirtualKey.Left or VirtualKey.Up when _fixOptions.Count > 0:
                _fixSel = (_fixSel + _fixOptions.Count - 1) % _fixOptions.Count;
                BuildFix(); Layout(); FocusChip();
                break;
            case VirtualKey.Right or VirtualKey.Down when _fixOptions.Count > 0:
                _fixSel = (_fixSel + 1) % _fixOptions.Count;
                BuildFix(); Layout(); FocusChip();
                break;
            case VirtualKey.Enter:
                ApplyFix(paste: !shift);
                break;
            case VirtualKey.Escape:
                EndFix(focus: true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// Подсветка: строка под мышью и выбранная стрелками; у них же видны кнопки.
    void Paint()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            var active = r.Hover || i == _sel;
            r.View.Background = Theme.Brush(i == _sel ? Theme.Selected : r.Hover ? Theme.Hover : Microsoft.UI.Colors.Transparent);
            r.Actions.Visibility = active && !_dragging ? Visibility.Visible : Visibility.Collapsed;
            r.Number.Visibility = active && !_dragging ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    async void LoadThumb(ClipEntry e, Image image)
    {
        if (_thumbs.TryGetValue(e.Id, out var cached)) { image.Source = cached; return; }
        try
        {
            var bytes = await File.ReadAllBytesAsync(_clips.Store.DataPath(e, ".png"));
            using var mem = new InMemoryRandomAccessStream();
            using (var w = new DataWriter(mem.GetOutputStreamAt(0)))
            {
                w.WriteBytes(bytes);
                await w.StoreAsync();
                await w.FlushAsync();
                w.DetachStream();
            }
            mem.Seek(0);
            var bmp = new BitmapImage { DecodePixelHeight = (int)(ThumbH * Win.Scale), DecodePixelType = DecodePixelType.Physical };
            await bmp.SetSourceAsync(mem);
            _thumbs[e.Id] = bmp;
            image.Source = bmp;
        }
        catch (Exception ex)
        {
            Log.Error("буфер: миниатюра", ex);
        }
    }

    // ---------- клавиши ----------

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = (GetKeyState(0x11) & 0x8000) != 0;
        var shift = (GetKeyState(0x10) & 0x8000) != 0;
        switch (e.Key)
        {
            case VirtualKey.Down when _rows.Count > 0:
                Select(Math.Min(_rows.Count - 1, _sel + 1));
                break;
            case VirtualKey.Up when _rows.Count > 0:
                Select(Math.Max(0, _sel - 1));
                break;
            case VirtualKey.PageDown when _rows.Count > 0:
                Select(Math.Min(_rows.Count - 1, _sel + 5));
                break;
            case VirtualKey.PageUp when _rows.Count > 0:
                Select(Math.Max(0, _sel - 5));
                break;
            case VirtualKey.Enter when _rows.Count > 0:
                Pick(_rows[_sel].Entry, plain: shift);
                break;
            case VirtualKey.T when ctrl && _rows.Count > 0 && _rows[_sel].Entry.Kind == ClipKind.Text:
                StartFix(_rows[_sel].Entry);
                break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number9 when ctrl:
            case >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 when ctrl:
            {
                var i = e.Key >= VirtualKey.NumberPad1 ? e.Key - VirtualKey.NumberPad1 : e.Key - VirtualKey.Number1;
                if (i < _rows.Count) Pick(_rows[i].Entry, plain: shift);
                break;
            }
            // Поле поиска пустое: Delete удаляет выбранный пункт (стирать в поле нечего).
            case VirtualKey.Delete when _search.Text.Length == 0 && _rows.Count > 0:
                _clips.Remove(_rows[_sel].Entry);
                break;
            case VirtualKey.Escape when _dragging:
                CancelDrag();
                break;
            case VirtualKey.Escape when _search.Text.Length > 0:
                _search.Text = "";
                break;
            case VirtualKey.Escape:
                Close(restoreFocus: true);
                break;
            case VirtualKey.Application:
            case VirtualKey.F10 when shift:
                if (_rows.Count > 0)
                {
                    var v = _rows[_sel].View;
                    ShowMenu(_rows[_sel], v.TransformToVisual(null).TransformPoint(new Point(24, 24)));
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void Select(int i)
    {
        _sel = i;
        Paint();
        _rows[i].View.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.5 });
    }

    /// Выбрали пункт: фокус окну, где печатали, окошко закрывается, пункт вставляется.
    void Pick(ClipEntry e, bool plain)
    {
        var target = _target;
        if (target != IntPtr.Zero && IsWindow(target)) SetForegroundWindow(target);
        Close(restoreFocus: false);
        _clips.Paste(e, plain, target);
    }

    void ShowMenu(RowView row, Point at)
    {
        var e = row.Entry;
        var items = new List<MenuItem>
        {
            new() { Text = "Вставить", Glyph = "", Invoke = () => Pick(e, plain: false) },
        };
        if (e.Kind == ClipKind.Text && (e.HasHtml || e.HasRtf))
            items.Add(new() { Text = "Вставить без оформления", Glyph = "", Invoke = () => Pick(e, plain: true) });
        items.Add(new() { Text = "Только скопировать", Glyph = "", Invoke = () => { _clips.PutBack(e, plain: false); Close(restoreFocus: true); } });
        if (e.Kind == ClipKind.Text) items.Add(new() { Text = "Преобразовать… (Ctrl+T)", Glyph = "\uE8D2", Invoke = () => StartFix(e) });
        items.Add(MenuItem.Sep);
        items.Add(new() { Text = e.Pinned ? "Открепить" : "Закрепить", Glyph = e.Pinned ? "" : "", Invoke = () => _clips.SetPinned(e, !e.Pinned) });
        items.Add(new() { Text = "Удалить", Glyph = "", Danger = true, Invoke = () => _clips.Remove(e) });
        var scale = Win.Scale;
        GetWindowRect(Hwnd, out var r);
        _menu.ShowInside(this, ("clip", e.Id), new Rect(r.Left + at.X * scale, r.Top + at.Y * scale, 1, 1), items);
    }

    // ---------- мышь: клик и перетаскивание закреплённых ----------

    void OnPressed(RowView row, PointerRoutedEventArgs a)
    {
        var p = a.GetCurrentPoint(_list);
        if (!p.Properties.IsLeftButtonPressed) return;
        _press = row;
        _pressY = p.Position.Y;
        _dragging = false;
        row.View.CapturePointer(a.Pointer);
        a.Handled = true;
    }

    void OnMoved(RowView row, PointerRoutedEventArgs a)
    {
        if (_press != row) return;
        var y = a.GetCurrentPoint(_list).Position.Y;
        var dy = y - _pressY;
        if (!_dragging)
        {
            // Тянуть можно только закреплённые и только когда не идёт поиск (иначе порядок не виден).
            if (!row.Entry.Pinned || _search.Text.Trim().Length > 0 || Math.Abs(dy) < 6) return;
            _dragging = true;
            Paint();
        }
        var pinned = _rows.Where(r => r.Entry.Pinned).ToList();
        var from = pinned.IndexOf(row);
        var top = Top(row);
        var h = row.View.ActualHeight + _list.Spacing;
        // Не дальше первого и последнего закреплённого.
        var minDy = Top(pinned[0]) - top;
        var maxDy = Top(pinned[^1]) + pinned[^1].View.ActualHeight - top - row.View.ActualHeight;
        dy = Math.Clamp(dy, minDy, maxDy);
        row.Shift.Y = dy;
        var center = top + dy + row.View.ActualHeight / 2;
        _dragTo = from;
        for (var i = 0; i < pinned.Count; i++)
        {
            if (i == from) continue;
            var other = pinned[i];
            var mid = Top(other) + other.View.ActualHeight / 2;
            if (i < from && center < mid) _dragTo = Math.Min(_dragTo, i);
            if (i > from && center > mid) _dragTo = Math.Max(_dragTo, i);
        }
        for (var i = 0; i < pinned.Count; i++)
        {
            if (i == from) continue;
            pinned[i].Shift.Y = i >= _dragTo && i < from ? h : i <= _dragTo && i > from ? -h : 0;
        }
    }

    /// Верх строки в списке без учёта сдвига перетаскиванием.
    double Top(RowView r) => r.View.TransformToVisual(_list).TransformPoint(new Point(0, 0)).Y - r.Shift.Y;

    void OnReleased(RowView row, PointerRoutedEventArgs a)
    {
        if (_press != row) return;
        var dragging = _dragging;
        var to = _dragTo;
        var inside = a.GetCurrentPoint(row.View).Position is var p && p.X >= 0 && p.Y >= 0 && p.X <= row.View.ActualWidth && p.Y <= row.View.ActualHeight;
        var shift = (GetKeyState(0x10) & 0x8000) != 0;
        // Сначала забываем нажатие, потом отпускаем мышь: потеря захвата приходит сразу.
        _press = null;
        _dragging = false;
        row.View.ReleasePointerCaptures();
        a.Handled = true;
        if (dragging)
        {
            foreach (var r in _rows) r.Shift.Y = 0;
            var from = _clips.Store.Pinned.IndexOf(row.Entry);
            if (to != from) _clips.MovePinned(row.Entry, to);
            else Paint();
            return;
        }
        if (inside) Pick(row.Entry, plain: shift);
    }

    void CancelDrag()
    {
        var row = _press;
        _press = null;
        _dragging = false;
        foreach (var r in _rows) r.Shift.Y = 0;
        row?.View.ReleasePointerCaptures();
        Paint();
    }

    protected override void OnClosing()
    {
        if (_press != null) CancelDrag();
        EndFix(focus: false);
        _menu.Close(restoreFocus: false);
    }
}
