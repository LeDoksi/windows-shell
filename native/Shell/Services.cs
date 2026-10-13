using Microsoft.UI.Dispatching;
using WindowsShell.Apps;
using WindowsShell.Audio;
using WindowsShell.Config;
using WindowsShell.Dock;
using WindowsShell.Media;
using WindowsShell.Native;
using WindowsShell.Ui;

namespace WindowsShell;

/// Что тащат между окнами оболочки: плитку категории или иконку дока.
public sealed record DragPayload(LauncherItem Item, string? FromCategory, string? FromPin = null);

/// Окно, на которое можно бросить плитку из другого окна оболочки (док, пилюля категории).
public interface IDropZone
{
    /// Курсор над окном (физические пиксели экрана). true — сюда можно бросить.
    bool DragOver(int x, int y, DragPayload payload);
    void DragLeave();
    /// Бросили. true — принято.
    bool Drop(int x, int y, DragPayload payload);
}

/// Общее для всех доков и панелей: системная часть и общие всплывающие окошки (одно меню,
/// одно окошко звука на все мониторы — открыто всегда только одно, правило 0.3).
public sealed class Services
{
    public required DispatcherQueue Queue { get; init; }
    public required ShellConfig Config { get; init; }
    public required MessageWindow Messages { get; init; }
    public required Monitors Monitors { get; init; }
    public required Ui.Bars Bars { get; init; }
    public required WindowTracker Tracker { get; init; }
    public required AudioService Audio { get; init; }
    public required MediaService Media { get; init; }
    public required Icons Icons { get; init; }

    MenuPopup? _menu;
    SoundFlyout? _sound;
    WindowListPopup? _list;
    Panel.CategoryWindow? _category;
    Panel.NameFlyout? _name;
    Panel.MediaFlyout? _mediaFlyout;

    public MenuPopup Menu => _menu ??= new MenuPopup();
    public SoundFlyout SoundFlyout => _sound ??= new SoundFlyout(Audio, Icons, Config);
    public WindowListPopup WindowList => _list ??= new WindowListPopup(Tracker, Icons);
    public Panel.CategoryWindow CategoryWindow => _category ??= new Panel.CategoryWindow(this);
    public Panel.NameFlyout NameFlyout => _name ??= new Panel.NameFlyout();
    public Panel.MediaFlyout MediaFlyout => _mediaFlyout ??= new Panel.MediaFlyout(Media);

    public List<IDropZone> DropZones { get; } = [];

    public AudioSettings AudioSettings => new(Config.Widget("audio"));

    /// Открыть Пуск (свой будет в WSH-E15; пока родной).
    public Action OpenStart { get; set; } = () => { };

    /// Открыть настройки виджетов (пункт меню трея).
    public Action OpenWidgetSettings { get; set; } = () => { };

    /// ⚙ на панели: открыть или закрыть настройки виджетов под кнопкой.
    public Action<Windows.Foundation.Rect, object> ToggleWidgetSettings { get; set; } = (_, _) => { };

    /// Переименовать категорию: поле под её пилюлей на панели монитора с курсором.
    public Action<string>? RenameCategory { get; set; }

    /// Закрепить программу в доке на место `index` (null — в конец закреплённых).
    public void PinApp(string title, string target, int? index = null)
    {
        Config.Update(c =>
        {
            if (c.Dock.Pinned.Any(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase))) return;
            var pin = new Pin(Config.NewId(title), title, target);
            var at = Math.Clamp(index ?? c.Dock.Pinned.Count, 0, c.Dock.Pinned.Count);
            c.Dock.Pinned.Insert(at, pin);
        });
    }

    public void Unpin(string id) => Config.Update(c => c.Dock.Pinned.RemoveAll(p => p.Id == id));

    /// Добавить плитки в категорию на место `index` (null — в конец). Повторы не добавляются.
    public void AddToCategory(string categoryId, IEnumerable<LauncherItem> items, int? index = null)
    {
        Config.Update(c =>
        {
            var cat = c.Categories.FirstOrDefault(x => x.Id == categoryId);
            if (cat is null) return;
            var at = Math.Clamp(index ?? cat.Items.Count, 0, cat.Items.Count);
            foreach (var item in items)
            {
                if (cat.Items.Any(i => string.Equals(i.Target, item.Target, StringComparison.OrdinalIgnoreCase))) continue;
                item.Id = Config.NewId(item.Title);
                cat.Items.Insert(at++, item);
            }
        });
    }
}
