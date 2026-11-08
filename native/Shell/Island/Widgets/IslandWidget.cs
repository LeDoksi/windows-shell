using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowsShell.Island.Widgets;

/// Виджет внутри тела острова (не отдельное окно). У каждого тела свои экземпляры: элементы
/// WinUI между окнами не переносим. Пока виджет закрыт, он не подписан на службы.
public abstract class IslandWidget
{
    protected readonly Services S;
    protected readonly IslandBody Body;
    protected readonly StackPanel Root = new() { Spacing = 12 };
    bool _open;

    protected IslandWidget(Services s, IslandBody body)
    {
        S = s;
        Body = body;
    }

    public abstract string Id { get; }

    /// Ширина тела с этим виджетом, DIP.
    public virtual double Width => 560;

    /// Большое состояние («лист»): размер тела в DIP на этом экране. null — обычный виджет.
    public virtual (double W, double H)? Sheet(WindowsShell.Native.Screen screen) => null;

    public FrameworkElement View => Root;

    public bool IsOpen => _open;

    public void Open()
    {
        if (_open) return;
        _open = true;
        try { OnOpen(); Rebuild(); }
        catch (Exception e) { Log.Error($"остров: открыть {Id}", e); }
    }

    public void Close()
    {
        if (!_open) return;
        _open = false;
        try { OnClose(); }
        catch (Exception e) { Log.Error($"остров: закрыть {Id}", e); }
    }

    protected virtual void OnOpen() { }
    protected virtual void OnClose() { }

    /// Пересобрать содержимое; тело подгоняет размер.
    protected void Rebuild()
    {
        if (!_open) return;
        try
        {
            Root.Children.Clear();
            Build();
        }
        catch (Exception e) { Log.Error($"остров: виджет {Id}", e); }
        Body.WidgetResized(this);
    }

    /// Содержимое поменялось без пересборки (раскрыли секцию) — подогнать размер.
    protected void Resized() => Body.WidgetResized(this);

    protected abstract void Build();

    /// Пересобрать в потоке интерфейса, если виджет открыт (события служб).
    protected void Later() => S.Queue.TryEnqueue(() => { if (_open) Rebuild(); });

    /// Первый элемент для фокуса с клавиатуры.
    public virtual void FocusFirst()
    {
        try { _ = Microsoft.UI.Xaml.Input.FocusManager.TryMoveFocus(Microsoft.UI.Xaml.Input.FocusNavigationDirection.Next, new Microsoft.UI.Xaml.Input.FindNextElementOptions { SearchRoot = Root.XamlRoot?.Content }); }
        catch { }
    }
}

public static class IslandWidgets
{
    public static IslandWidget Create(string id, Services s, IslandBody body) => id switch
    {
        "sound" => new SoundWidget(s, body),
        "media" => new MediaWidget(s, body),
        "net" => new NetWidget(s, body),
        "charge" => new ChargeWidget(s, body),
        "shots" => new ShotsWidget(s, body),
        "bin" => new BinWidget(s, body),
        "shelf" => new ShelfWidget(s, body),
        "settings" => new SettingsWidget(s, body),
        _ => new LangWidget(s, body),
    };
}
