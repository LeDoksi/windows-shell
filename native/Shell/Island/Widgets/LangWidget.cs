using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Apps;
using WindowsShell.Tail;
using static WindowsShell.Island.IslandUi;

namespace WindowsShell.Island.Widgets;

/// Раскладка в острове: раскладки кнопками, текущая отмечена. Меняется в окне, где работали
/// до острова (как выбор в списке Windows), а не в самом острове.
public sealed class LangWidget(Services s, IslandBody body) : IslandWidget(s, body)
{
    public override string Id => "lang";
    public override double Width => 340;

    protected override void OnOpen() => S.Language.Changed += Later;
    protected override void OnClose() => S.Language.Changed -= Later;

    protected override void Build()
    {
        var current = S.Language.Current;
        Root.Children.Add(Header("Раскладка", out var right));
        right.Text = current.Short;

        var list = new StackPanel { Spacing = 2 };
        var all = InputLanguage.All();
        if (all.Count == 0) list.Children.Add(new Border { Padding = new Thickness(12, 10, 12, 10), Child = Text("Раскладки не найдены", 13, Sub) });
        foreach (var l in all)
        {
            var on = l.Hkl == current.Hkl;
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tag = Text(l.Short, 14, on ? Ink : Sub, bold: true);
            tag.CharacterSpacing = 40;
            g.Children.Add(tag);
            var name = Text(l.Name, 14);
            ToolTipService.SetToolTip(name, l.Name);
            Grid.SetColumn(name, 1);
            g.Children.Add(name);
            if (on)
            {
                var check = Glyph("", 12);
                Grid.SetColumn(check, 2);
                g.Children.Add(check);
            }
            var b = Row(g, on, name: l.Name);
            var layout = l;
            b.Click += (_, _) =>
            {
                S.Language.Select(Body.WorkWindow, layout);
                // Раскладку выбрали — остров больше не нужен, фокус возвращается в окно.
                Body.Sleep();
            };
            list.Children.Add(b);
        }
        Root.Children.Add(GroupBox(list));
        Root.Children.Add(Link("Параметры языка ↗", () => { Body.Sleep(restoreFocus: false); Launcher.Open("ms-settings:regionlanguage"); }));
    }
}
