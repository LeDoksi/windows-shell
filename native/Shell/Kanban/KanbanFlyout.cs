using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using static WindowsShell.Tail.FlyoutUi;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Kanban;

/// Окошко над значком канбана (design-v2 п. 9а): только быстрая задача. Все проекты списком,
/// поле «Новая задача…», по желанию эпик; Enter — задача в бэклог, внизу «✓ WSH-123 создана»,
/// и окошко закрывается. ↑/↓ в поле — другой проект.
public sealed class KanbanFlyout : Popup
{
    static readonly Windows.UI.Color Green = Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F);

    readonly KanbanService _k;
    readonly MenuPopup _menu = new();
    readonly StackPanel _body = new() { Padding = new Thickness(14, 12, 14, 12), Spacing = 10 };
    readonly TextBox _input;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _closeTimer;
    string? _project, _epic;
    (string Text, Windows.UI.Color Color)? _note;
    bool _busy;

    public KanbanFlyout(KanbanService kanban) : base(activating: true)
    {
        _k = kanban;
        _input = new TextBox
        {
            PlaceholderText = "Новая задача…",
            FontFamily = Theme.Font,
            FontSize = 14,
            MaxLength = 300,
        };
        _input.PreviewKeyDown += OnInputKey;
        _input.TextChanged += (_, _) => { if (_input.Text.Length > 0) _closeTimer?.Stop(); };
        SetContent(_body);
        _closeTimer = Win.Window.DispatcherQueue.CreateTimer();
        _closeTimer.Interval = TimeSpan.FromMilliseconds(1600);
        _closeTimer.IsRepeating = false;
        _closeTimer.Tick += (_, _) => { if (IsOpen && _input.Text.Length == 0) Close(restoreFocus: true); };
        _k.Changed += () =>
        {
            if (!IsOpen) return;
            var focused = ReferenceEquals(FocusManager.GetFocusedElement(Win.Root.XamlRoot), _input);
            Build();
            Layout();
            if (focused) _input.Focus(FocusState.Programmatic);
        };
    }

    protected override double Width => 340;
    protected override double Gap => 8;

    protected override Size Measure(double width)
    {
        _body.Measure(new Size(width, double.PositiveInfinity));
        return _body.DesiredSize;
    }

    public void Show(object anchor, Rect anchorPx, object owner)
    {
        _note = null;
        _closeTimer.Stop();
        var s = _k.Settings;
        _project = _k.Project(s.QuickProject)?.Id ?? _k.Project(s.Project)?.Id ?? _k.Projects.FirstOrDefault()?.Id;
        _epic = _project is null ? null : ValidEpic(_project, s.Epic(_project));
        Build();
        Open(anchor, anchorPx, Placement.Above, owner: owner);
        if (_k.Api.SignedIn) _input.Focus(FocusState.Programmatic);
        _ = _k.Refresh();
    }

    string? ValidEpic(string project, string? epic) => _k.OpenEpics(project).Any(e => e.Id == epic) ? epic : null;

    void Build()
    {
        _body.Children.Clear();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Label("Новая задача", 15, bold: true));
        var open = new HyperlinkButton { Content = Label("Открыть канбан ↗", 13), Padding = new Thickness(4, 2, 4, 2) };
        open.Click += (_, _) => { Close(restoreFocus: false); _k.OpenWindow(); };
        Grid.SetColumn(open, 1);
        head.Children.Add(open);
        _body.Children.Add(head);

        if (!_k.Api.SignedIn)
        {
            _body.Children.Add(SignInView.Build(_k));
            return;
        }
        if (_k.Projects.Count == 0)
        {
            _body.Children.Add(Label(_k.Error ?? (_k.Loading || _k.LoadedAt is null ? "Загружаю проекты…" : "Проектов нет."), 13, secondary: true));
            if (_k.Error != null) _body.Children.Add(Link("Повторить", () => _ = _k.Refresh()));
            return;
        }
        if (_k.Project(_project) is null)
        {
            // Проекты только что прочитались: открываем тот, куда добавляли в прошлый раз.
            var s = _k.Settings;
            _project = _k.Project(s.QuickProject)?.Id ?? _k.Project(s.Project)?.Id ?? _k.Projects[0].Id;
            _epic = ValidEpic(_project, s.Epic(_project));
        }

        var projects = new StackPanel { Spacing = 2 };
        foreach (var p in _k.Projects) projects.Children.Add(ProjectRow(p));
        _body.Children.Add(projects);

        _input.IsEnabled = !_busy;
        _body.Children.Add(_input);

        var epic = _k.Epic(_epic);
        var pill = new Button
        {
            Content = Label(epic is null ? "без эпика ▾" : $"{epic.Short} {epic.Title} ▾", 12),
            FontSize = 12,
            Padding = new Thickness(10, 3, 10, 4),
            CornerRadius = new CornerRadius(12),
            MaxWidth = 260,
        };
        pill.Click += (_, _) => EpicMenu(pill);
        var epicRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Label("Эпик:", 12, secondary: true), pill } };
        _body.Children.Add(epicRow);

        var note = _note ?? (_k.Error is { } err ? (err, Theme.Warning) : ("Enter — в бэклог · ↑↓ — проект", Theme.TextTertiary));
        var text = Label(note.Text, 12);
        text.Foreground = Theme.Brush(note.Color);
        text.TextWrapping = TextWrapping.Wrap;
        _body.Children.Add(text);
    }

    Button ProjectRow(KProject p)
    {
        var on = p.Id == _project;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = Label(p.Name, 13);
        if (!on) name.Foreground = Theme.Brush(Theme.TextSecondary);
        grid.Children.Add(name);
        var key = Label(p.Prefix, 11, secondary: true);
        Grid.SetColumn(key, 1);
        grid.Children.Add(key);
        var b = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(9, 5, 9, 5),
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush(on ? Windows.UI.Color.FromArgb(0x24, 0x4C, 0xC2, 0xFF) : Microsoft.UI.Colors.Transparent),
            BorderBrush = Theme.Brush(on ? Windows.UI.Color.FromArgb(0x73, 0x4C, 0xC2, 0xFF) : Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1),
            IsTabStop = false,
        };
        b.Click += (_, _) => Select(p.Id);
        return b;
    }

    void Select(string project)
    {
        if (project == _project) { _input.Focus(FocusState.Programmatic); return; }
        _project = project;
        _epic = ValidEpic(project, _k.Settings.Epic(project));
        _note = null;
        Build();
        Layout();
        _input.Focus(FocusState.Programmatic);
    }

    void EpicMenu(FrameworkElement pill)
    {
        if (_project is null) return;
        var project = _project;
        var items = new List<MenuItem> { new() { Text = "Без эпика", Checked = _epic is null, Invoke = () => SetEpic(null) } };
        var epics = _k.OpenEpics(project);
        if (epics.Count > 0) items.Add(MenuItem.Sep);
        foreach (var e in epics)
        {
            var id = e.Id;
            items.Add(new() { Text = $"{e.Short} {e.Title}", Checked = _epic == id, Invoke = () => SetEpic(id) });
        }
        var scale = Win.Scale;
        GetWindowRect(Hwnd, out var r);
        var at = pill.TransformToVisual(null).TransformPoint(new Point(0, 0));
        _menu.ShowInside(this, ("kanban-epic", project), new Rect(r.Left + at.X * scale, r.Top + at.Y * scale, pill.ActualWidth * scale, pill.ActualHeight * scale), items);
    }

    void SetEpic(string? epic)
    {
        _epic = epic;
        Build();
        Layout();
        _input.Focus(FocusState.Programmatic);
    }

    void OnInputKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                Submit();
                break;
            case VirtualKey.Up or VirtualKey.Down when _k.Projects.Count > 1:
            {
                e.Handled = true;
                var i = _k.Projects.FindIndex(p => p.Id == _project);
                var next = (i + (e.Key == VirtualKey.Down ? 1 : -1) + _k.Projects.Count) % _k.Projects.Count;
                Select(_k.Projects[next].Id);
                break;
            }
        }
    }

    async void Submit()
    {
        var title = _input.Text.Trim();
        if (title.Length == 0 || _busy || _project is null) return;
        var project = _project;
        var epic = _epic;
        _busy = true;
        _note = ("Создаю…", Theme.TextSecondary);
        Build();
        Layout();
        try
        {
            var item = await _k.Create(project, title, epic);
            _input.Text = "";
            _note = ($"✓ {item.Id} «{(title.Length > 40 ? title[..40] + "…" : title)}» создана", Green);
            _k.RememberQuick(project, epic);
            _closeTimer.Start();
        }
        catch (KanbanException e) { _note = (e.Message, Theme.Warning); }
        catch (Exception e)
        {
            Log.Write($"канбан: быстрая задача: {e}");
            _note = ("Задача не создалась, подробности в журнале оболочки.", Theme.Warning);
        }
        finally
        {
            _busy = false;
            if (IsOpen)
            {
                Build();
                Layout();
                _input.Focus(FocusState.Programmatic);
            }
        }
    }
}
