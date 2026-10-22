using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WindowsShell.Ui;
using static WindowsShell.Kanban.KanbanWindow;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Kanban;

/// Открытая карточка справа в окне канбана: название, статус, эпик, описание, шаги и
/// комментарии; всё правится на месте и сразу пишется в канбан.
sealed class CardPanel
{
    readonly KanbanService _k;
    readonly StackPanel _body = new() { Spacing = 10, Padding = new Thickness(16, 14, 16, 20) };
    KDetails? _details;
    List<KComment>? _comments;
    string? _error;
    int _load;
    TextBox? _title, _text;
    ComboBox? _status, _epic;
    bool _sync;

    public Border Root { get; }
    public KItem? Current { get; private set; }
    public bool Open => Current != null;

    /// Карточку закрыли.
    public event Action? Closed;

    public CardPanel(KanbanService kanban)
    {
        _k = kanban;
        Root = new Border
        {
            Width = 380,
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(KanbanWindow.Line),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
    }

    public void Show(KItem item)
    {
        if (Current?.Id == item.Id) return;
        Commit();
        Current = item;
        _details = null;
        _comments = null;
        _error = null;
        Root.Visibility = Visibility.Visible;
        Build();
        Load(item.Id);
    }

    public void Hide()
    {
        if (Current is null) return;
        Commit();
        Current = null;
        _load++;
        Root.Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    /// Данные перечитали: карточка — свежий объект, поля, которые сейчас не правят, обновляются.
    public void Sync()
    {
        if (Current is null) return;
        if (_k.Item(Current.Id) is { } fresh) Current = fresh;
        _sync = true;
        try
        {
            if (_title != null && _title.FocusState == FocusState.Unfocused && _title.Text != Current.Title) _title.Text = Current.Title;
            if (_status != null) _status.SelectedIndex = Array.IndexOf(KStatus.All, Current.Status);
            if (_epic != null) _epic.SelectedIndex = Math.Max(0, EpicChoices().FindIndex(e => e?.Id == Current.EpicId));
        }
        finally { _sync = false; }
    }

    async void Load(string id)
    {
        var my = ++_load;
        try
        {
            var details = _k.Api.Details(id);
            var comments = _k.Api.Comments(id);
            await Task.WhenAll(details, comments);
            if (my != _load) return;
            _details = details.Result;
            _comments = comments.Result;
        }
        catch (Exception e)
        {
            if (my != _load) return;
            if (e is not KanbanException) Log.Write($"канбан: карточка {id}: {e}");
            _error = e is KanbanException k ? k.Message : "Карточка не загрузилась, подробности в журнале оболочки.";
        }
        Build();
    }

    /// Ушли с поля, не нажав Enter: сохраняем, что ввели.
    void Commit()
    {
        if (Current is not { } item) return;
        if (_title != null && _title.Text.Trim() is { Length: > 0 } t && t != item.Title) _ = _k.Rename(item, t);
        if (_text != null && _details != null && _text.Text != (_details.Body ?? "")) SaveBody(item.Id, _text.Text);
    }

    List<KEpic?> EpicChoices()
    {
        var list = new List<KEpic?> { null };
        if (Current is null) return list;
        list.AddRange(_k.Epics.Where(e => e.ProjectId == Current.ProjectId && (e.Status == "active" || e.Id == Current.EpicId)).OrderBy(e => e.Seq));
        return list;
    }

    void Build()
    {
        _body.Children.Clear();
        _title = _text = null;
        _status = _epic = null;
        if (Current is not { } item) return;

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var type = item.Type switch { "bug" => "ошибка", "chore" => "дело", _ => "задача" };
        head.Children.Add(Text(12, Muted, $"{item.Id} · {type}"));
        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontFamily = Theme.Icons, FontSize = 12 },
            Width = 32, Height = 30, Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(close, "Закрыть (Esc)");
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        _body.Children.Add(head);

        _title = new TextBox
        {
            Text = item.Title,
            FontFamily = Theme.Font,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = 300,
        };
        var title = _title;
        title.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Root.Focus(FocusState.Programmatic); } };
        title.LostFocus += (_, _) =>
        {
            var t = title.Text.Trim();
            if (Current is { } c && t.Length > 0 && t != c.Title) _ = _k.Rename(c, t);
        };
        _body.Children.Add(title);

        _status = new ComboBox { Header = "Статус", MinWidth = 150 };
        foreach (var s in KStatus.All) _status.Items.Add(KStatus.Name(s));
        _epic = new ComboBox { Header = "Эпик", MinWidth = 170, MaxWidth = 190 };
        foreach (var e in EpicChoices()) _epic.Items.Add(e is null ? "без эпика" : $"{e.Short} {e.Title}");
        _sync = true;
        _status.SelectedIndex = Array.IndexOf(KStatus.All, item.Status);
        _epic.SelectedIndex = Math.Max(0, EpicChoices().FindIndex(e => e?.Id == item.EpicId));
        _sync = false;
        _status.SelectionChanged += (_, _) =>
        {
            if (_sync || Current is not { } c || _status.SelectedIndex < 0) return;
            var to = KStatus.All[_status.SelectedIndex];
            if (to == c.Status) return;
            var column = _k.Column(c.ProjectId, to).Where(i => i.Id != c.Id).ToList();
            _ = _k.Move(c, to, KanbanService.PositionAt(column, column.Count));
        };
        _epic.SelectionChanged += (_, _) =>
        {
            if (_sync || Current is not { } c || _epic.SelectedIndex < 0) return;
            var to = EpicChoices().ElementAtOrDefault(_epic.SelectedIndex)?.Id;
            if (to != c.EpicId) _ = _k.SetEpic(c, to);
        };
        _body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _status, _epic } });

        var meta = new List<string> { item.CreatedBy == "claude" ? "создал Claude" : "создал ты" };
        if (item.UpdatedAt is { } u) meta.Add($"изменена {u.ToLocalTime():dd.MM HH:mm}");
        if (item.ClosedAt is { } cl) meta.Add($"готова {cl.ToLocalTime():dd.MM}");
        _body.Children.Add(Text(11, Faint, string.Join(" · ", meta)));

        if (_error != null)
        {
            var err = Text(12, Theme.Warning, _error);
            err.TextWrapping = TextWrapping.Wrap;
            _body.Children.Add(err);
        }
        if (_details is null)
        {
            if (_error is null) _body.Children.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Left });
            _body.Children.Add(SiteLink());
            return;
        }

        _body.Children.Add(Section("Описание"));
        _text = new TextBox
        {
            Text = _details.Body ?? "",
            PlaceholderText = "Описание…",
            FontFamily = Theme.Font,
            FontSize = 13,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            MaxHeight = 360,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        var text = _text;
        text.LostFocus += (_, _) =>
        {
            if (Current is { } c && _details != null && text.Text != (_details.Body ?? "")) SaveBody(c.Id, text.Text);
        };
        _body.Children.Add(text);

        var checks = _details.Checklist;
        _body.Children.Add(Section(checks.Count > 0 ? $"Шаги · {checks.Count(x => x.Done)}/{checks.Count}" : "Шаги"));
        for (var n = 0; n < checks.Count; n++)
        {
            var index = n;
            var box = new CheckBox { Content = Wrap(checks[n].Text), IsChecked = checks[n].Done, MinHeight = 28, Padding = new Thickness(6, 4, 0, 0) };
            box.Click += (_, _) =>
            {
                if (_details is null || index >= _details.Checklist.Count) return;
                _details.Checklist[index].Done = box.IsChecked == true;
                SaveChecklist();
            };
            _body.Children.Add(box);
        }
        var step = new TextBox { PlaceholderText = "Новый шаг… Enter", FontFamily = Theme.Font, FontSize = 13 };
        step.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || step.Text.Trim().Length == 0 || _details is null) return;
            e.Handled = true;
            _details.Checklist.Add(new KCheck { Text = step.Text.Trim() });
            SaveChecklist();
            Build();
        };
        _body.Children.Add(step);

        _body.Children.Add(Section(_comments is { Count: > 0 } cs ? $"Комментарии · {cs.Count}" : "Комментарии"));
        foreach (var c in _comments ?? [])
        {
            var who = Text(11, c.Author == "claude" ? Violet : Theme.Accent, $"{(c.Author == "claude" ? "Claude" : "Ты")} · {c.CreatedAt.ToLocalTime():dd.MM HH:mm}");
            var body = new TextBlock
            {
                Text = c.Body, FontFamily = Theme.Font, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            };
            _body.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x0A, 255, 255, 255)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 8),
                Child = new StackPanel { Spacing = 3, Children = { who, body } },
            });
        }
        var comment = new TextBox
        {
            PlaceholderText = "Комментарий… Ctrl+Enter — отправить",
            FontFamily = Theme.Font, FontSize = 13, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60,
        };
        var send = new Button { Content = "Отправить", HorizontalAlignment = HorizontalAlignment.Right };
        async void Send()
        {
            var t = comment.Text.Trim();
            if (t.Length == 0 || Current is not { } cur || !send.IsEnabled) return;
            send.IsEnabled = false;
            try
            {
                var added = await _k.Api.AddComment(cur.Id, t);
                if (Current?.Id != cur.Id) return;
                (_comments ??= []).Add(added);
                _error = null;
            }
            catch (Exception e)
            {
                if (e is not KanbanException) Log.Write($"канбан: комментарий {cur.Id}: {e}");
                _error = e is KanbanException k ? k.Message : "Комментарий не отправился, подробности в журнале оболочки.";
                send.IsEnabled = true;
                Build();
                return;
            }
            Build();
        }
        send.Click += (_, _) => Send();
        comment.KeyDown += (_, e) =>
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (e.Key == VirtualKey.Enter && ctrl) { e.Handled = true; Send(); }
        };
        _body.Children.Add(comment);
        _body.Children.Add(send);
        _body.Children.Add(SiteLink());
    }

    async void SaveBody(string id, string text)
    {
        if (_details != null && Current?.Id == id) _details.Body = text;
        var error = await _k.Save(id, new JsonObject { ["body"] = text });
        if (error != null && Current?.Id == id) { _error = error; Build(); }
    }

    async void SaveChecklist()
    {
        if (Current is not { } item || _details is null) return;
        var array = new JsonArray(_details.Checklist.Select(c => (JsonNode)new JsonObject { ["text"] = c.Text, ["done"] = c.Done }).ToArray());
        var error = await _k.Save(item.Id, new JsonObject { ["checklist"] = array });
        if (error != null && Current?.Id == item.Id) _error = error;
        if (Current?.Id == item.Id) Build();
    }

    UIElement SiteLink()
    {
        var link = new HyperlinkButton { Content = Text(12, Theme.Accent, "Сайт канбана ↗"), Padding = new Thickness(0, 4, 0, 4) };
        link.Click += (_, _) => _k.OpenSite();
        return link;
    }

    static TextBlock Section(string text)
    {
        var t = Text(12, Muted, text);
        t.Margin = new Thickness(0, 6, 0, 0);
        return t;
    }

    static TextBlock Wrap(string text) => new()
    {
        Text = text, FontFamily = Theme.Font, FontSize = 13, TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
    };
}
