using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsShell.Shots;
using static WindowsShell.Island.IslandUi;
using Color = Windows.UI.Color;

namespace WindowsShell.Island.Widgets;

/// Снимки и запись в острове (эскиз ShotsOcr). Сверху — «Снимок», «Запись» и «Текст»; во время
/// записи — управление ею (стоп, пауза, микрофон, звук системы). Ниже — галерея: сегодня или
/// всё за 3 дня, 4 в ряд; клик выбирает, у выбранного — действия; миниатюру можно перетащить
/// в любое окно (StartDragAsync). Распознанный текст (OCR, волна 4) сначала виден здесь: его
/// можно поправить и только потом скопировать.
public sealed class ShotsWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    const int Cols = 4;
    const double TileH = 72;
    static readonly Color RecBg = Color.FromArgb(255, 0x2A, 0x14, 0x14);
    static readonly Color StopBg = Color.FromArgb(255, 0x3A, 0x15, 0x15);
    static readonly Color Selected = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);

    string? _selected;
    bool _all;
    TextBlock? _time;

    public override string Id => "shots";
    public override double Width => 602;

    TextBox? _ocrBox;

    protected override void OnOpen()
    {
        S.Shots.Changed += Later;
        S.Shots.OcrChanged += Later;
        S.Shots.Tick += OnTick;
        S.Shots.Refresh();
    }

    protected override void OnClose()
    {
        S.Shots.Changed -= Later;
        S.Shots.OcrChanged -= Later;
        S.Shots.Tick -= OnTick;
        // Закрыли остров, не скопировав, — результат больше не нужен.
        S.Shots.DropOcr();
        _ocrBox = null;
    }

    public override void FocusFirst()
    {
        if (_ocrBox != null) { _ocrBox.Focus(FocusState.Programmatic); return; }
        base.FocusFirst();
    }

    void OnTick() => S.Queue.TryEnqueue(() =>
    {
        if (_time != null) _time.Text = $"Стоп · {ShotsService.Length(S.Shots.RecordingTime)}";
    });

    protected override void Build()
    {
        var shots = S.Shots;
        _time = null;
        _ocrBox = null;
        if (shots.Ocr is { } job)
        {
            BuildOcr(job);
            return;
        }
        if (!shots.Enabled)
        {
            Root.Children.Add(Header("Снимки", out _));
            Root.Children.Add(Text("Снимки и запись выключены — PrtSc сейчас работает как в Windows.", 13, Sub, wrap: true));
            Root.Children.Add(Btn("Включить", primary: true, () => S.Config.Update(_ => S.Config.Widget("shots").Enabled = true)));
            return;
        }

        if (shots.Recording) Root.Children.Add(RecordingControls());
        else
        {
            var big = new Grid { ColumnSpacing = 8 };
            big.ColumnDefinitions.Add(new ColumnDefinition());
            big.ColumnDefinitions.Add(new ColumnDefinition());
            big.ColumnDefinitions.Add(new ColumnDefinition());
            big.Children.Add(Big("Снимок", "PrtSc · область, окно", Group, Ink, () => Start(record: false)));
            var rec = Big("● Запись", "Shift+PrtSc", RecBg, RedText, () => Start(record: true));
            Grid.SetColumn(rec, 1);
            big.Children.Add(rec);
            var text = Big("Текст", "Ctrl+PrtSc · с экрана", Group, Ink, StartText);
            Grid.SetColumn(text, 2);
            big.Children.Add(text);
            Root.Children.Add(big);
        }

        var today = shots.Items.Where(i => i.Time.Date == DateTime.Today).ToList();
        var list = _all || today.Count == 0 ? shots.Items.ToList() : today;
        var head = new Grid();
        head.Children.Add(Text(_all || today.Count == 0 ? "ЗА 3 ДНЯ" : "СЕГОДНЯ", 13, Sub, bold: true));
        if (shots.Items.Count > today.Count && today.Count > 0)
        {
            var more = Link(_all ? "Только сегодня" : $"Всё за 3 дня · {shots.Items.Count}", () => { _all = !_all; Rebuild(); });
            head.Children.Add(more);
        }
        Root.Children.Add(head);

        if (list.Count == 0)
        {
            Root.Children.Add(Text("Пока пусто. Снимки и записи хранятся 3 дня, потом удаляются сами.", 13, Sub, wrap: true));
        }
        else
        {
            if (_selected == null || list.All(i => i.Path != _selected)) _selected = list[0].Path;
            Root.Children.Add(Gallery(list.Take(_all ? 24 : 12).ToList()));
            if (list.FirstOrDefault(i => i.Path == _selected) is { } sel) Root.Children.Add(Actions(sel));
            Root.Children.Add(Text("Перетащи миниатюру в любое окно или в папку.", 12, Faint));
        }
        Root.Children.Add(Link("Папка снимков ↗", () => { Body.Sleep(restoreFocus: false); ShotsService.OpenFolder(); }));
    }

    /// Остров сворачивается, потом — выбор области (сам остров в снимок не попадает, но не должен
    /// закрывать окна под собой, пока выбираешь).
    void Start(bool record)
    {
        Body.Sleep(restoreFocus: true);
        var t = S.Queue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(180);
        t.IsRepeating = false;
        t.Tick += (_, _) => { if (record) S.Shots.Record(); else S.Shots.Snap(); };
        t.Start();
    }

    void StartText()
    {
        Body.Sleep(restoreFocus: true);
        var t = S.Queue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(180);
        t.IsRepeating = false;
        t.Tick += (_, _) => S.Shots.SnapText();
        t.Start();
    }

    /// Результат распознавания: язык, правка, «Копировать» (И6: в буфер только после проверки).
    void BuildOcr(OcrJob job)
    {
        Root.Children.Add(Header("Текст с картинки", out var right));
        right.Text = job.Source;
        var langs = Ocr.Languages();
        if (langs.Count > 1)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var (tag, name) in langs.Prepend((Ocr.Auto, "Авто")))
            {
                var on = string.Equals(tag, job.Lang, StringComparison.OrdinalIgnoreCase);
                var b = Btn((on ? "✓ " : "") + name, click: () => { if (!on) S.Shots.OcrLanguage(tag); });
                b.Opacity = on ? 1 : 0.65;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"Язык распознавания: {name}{(on ? ", выбран" : "")}");
                row.Children.Add(b);
            }
            Root.Children.Add(row);
        }
        if (job.Text.Length == 0)
        {
            Root.Children.Add(Text(job.Error ?? "Текст не найден.", 13, AmberText, wrap: true));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (langs.Count == 0) buttons.Children.Add(Btn("Языки Windows ↗", click: () => { Body.Sleep(restoreFocus: false); Apps.Launcher.Open("ms-settings:regionlanguage"); }));
            buttons.Children.Add(Btn("Ещё раз с экрана", click: () => { S.Shots.DropOcr(); StartText(); }));
            buttons.Children.Add(Btn("Закрыть", click: () => { S.Shots.DropOcr(); Body.Sleep(); }));
            Root.Children.Add(buttons);
            return;
        }
        var box = new TextBox
        {
            Text = job.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 320,
            FontSize = 14,
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Распознанный текст, можно поправить");
        _ocrBox = box;
        Root.Children.Add(box);
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(Text("Проверь и поправь — в буфер попадёт то, что в поле.", 12, Faint, wrap: true));
        var cancel = Btn("Отмена", click: () => { S.Shots.DropOcr(); Body.Sleep(); });
        Grid.SetColumn(cancel, 1);
        actions.Children.Add(cancel);
        var copy = Btn("Копировать", primary: true, click: () =>
        {
            if (S.Shots.CopyOcr(box.Text)) Body.Sleep();
        });
        Grid.SetColumn(copy, 2);
        actions.Children.Add(copy);
        Root.Children.Add(actions);
    }

    static Button Big(string title, string sub, Color bg, Color fg, Action click)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Text(title, 15, fg, bold: true));
        stack.Children.Add(Text(sub, 12, Sub));
        var b = new Button
        {
            Content = stack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = B(bg),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"{title.TrimStart('●', ' ')} — {sub}");
        b.Click += (_, _) => click();
        return b;
    }

    UIElement RecordingControls()
    {
        var shots = S.Shots;
        var box = new StackPanel { Spacing = 10 };
        box.Children.Add(Header("Идёт запись", out var right, shots.RecordingPaused ? AmberText : RedText));
        right.Text = shots.RecordingPaused ? "на паузе" : "";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var stop = Btn("", click: () => shots.StopRecording());
        stop.Background = B(StopBg);
        _time = Text($"Стоп · {ShotsService.Length(shots.RecordingTime)}", 13, RedText, bold: true);
        stop.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Text("■", 13, RedText), _time } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stop, "Остановить запись");
        row.Children.Add(stop);
        row.Children.Add(Btn(shots.RecordingPaused ? "▶ Продолжить" : "❚❚ Пауза", click: () => { shots.TogglePause(); Rebuild(); }));
        row.Children.Add(Toggle("Микрофон", shots.RecordMic, on => shots.SetMic(on)));
        row.Children.Add(Toggle("Звук системы", shots.RecordSystemSound, on => shots.SetSystemSound(on)));
        box.Children.Add(row);
        box.Children.Add(Text("Ещё раз Shift+PrtSc — тоже стоп.", 12, Faint));
        return GroupBox(new Border { Padding = new Thickness(10), Child = box });
    }

    Button Toggle(string text, bool on, Action<bool> set)
    {
        var b = Btn((on ? "✓ " : "") + text, primary: false);
        b.Opacity = on ? 1 : 0.6;
        b.Click += (_, _) => { set(!on); Rebuild(); };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, $"{text}: {(on ? "включён" : "выключен")}");
        return b;
    }

    Grid Gallery(List<ShotItem> items)
    {
        var g = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (var c = 0; c < Cols; c++) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < items.Count; i++)
        {
            if (i % Cols == 0) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileH) });
            var tile = Tile(items[i]);
            Grid.SetRow(tile, i / Cols);
            Grid.SetColumn(tile, i % Cols);
            g.Children.Add(tile);
        }
        return g;
    }

    FrameworkElement Tile(ShotItem item)
    {
        var image = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _ = Load(item, image);
        var sel = item.Path == _selected;
        var tile = new Grid
        {
            CornerRadius = new CornerRadius(10),
            Background = B(RowOn),
            BorderBrush = B(sel ? Selected : Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(2),
            Children = { image },
        };
        if (!item.IsImage)
        {
            tile.Children.Add(new Border
            {
                Background = B(Color.FromArgb(0x99, 0, 0, 0)), CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = Text(item.Kind == ShotKind.Gif ? "GIF" : "▶ видео", 10, bold: true),
            });
        }
        ToolTipService.SetToolTip(tile, $"{item.Name}\n{item.Time:dd.MM HH:mm}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, $"{(item.IsImage ? "Снимок" : "Запись")} {item.Time:HH:mm}");
        Draggable(tile, () => item.Path, () =>
        {
            // Клик по выбранной — открыть, по другой — выбрать.
            if (_selected == item.Path) { Body.Sleep(restoreFocus: false); S.Shots.Edit(item); }
            else { _selected = item.Path; Rebuild(); }
        }, () => Body.Sleep(restoreFocus: false));
        return tile;
    }

    static async Task Load(ShotItem item, Image image)
    {
        try { image.Source = await ShotImages.Load(item, 220); }
        catch { }
    }

    UIElement Actions(ShotItem item)
    {
        var wrap = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemHeight = 40 };
        void Add(string text, Action a, bool close = true)
        {
            var b = Btn(text, click: () => { if (close) Body.Sleep(restoreFocus: false); a(); });
            b.Margin = new Thickness(0, 0, 6, 6);
            wrap.Children.Add(b);
        }
        Add("Открыть", () => Apps.Launcher.Open(item.Path));
        if (item.IsImage) Add("Изменить", () => S.Shots.Edit(item));
        if (item.IsImage) Add("Текст", () => S.Shots.TextOf(item), close: false);
        Add("Копировать", () => S.Shots.Copy(item), close: false);
        Add("Показать в папке", () => S.Shots.Reveal(item));
        Add("Сохранить как…", () => S.Shots.SaveAs(item, Body.Hwnd), close: false);
        if (item.Kind == ShotKind.Video) Add("GIF", () => S.Shots.SaveGif(item), close: false);
        Add("Удалить", () => { S.Shots.Delete(item); _selected = null; }, close: false);
        return wrap;
    }
}
