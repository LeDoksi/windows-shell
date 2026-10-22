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
    public required WinKeys Keys { get; init; }
    public required Start.Catalog Catalog { get; init; }
    public required Tray.TrayHost Tray { get; init; }
    public required Tray.TaskbarButtons Buttons { get; init; }
    public required Tail.NetworkService Network { get; init; }
    public required Tail.BatteryService Battery { get; init; }
    public required Tail.InputLanguage Language { get; init; }
    public required Tail.Notifications Notifications { get; init; }
    public required Tail.RecycleBin Bin { get; init; }
    public required Shots.ShotsService Shots { get; init; }
    public required Clips.ClipsService Clips { get; init; }
    public required Charge.ChargeService Charge { get; init; }
    public required Perf.PerfService Perf { get; init; }

    MenuPopup? _menu;
    SoundFlyout? _sound;
    WindowListPopup? _list;
    Tail.MediaFlyout? _mediaFlyout;
    Start.StartMenu? _start;
    Tray.TrayFlyout? _trayFlyout;
    Tail.NetworkFlyout? _netFlyout;
    Tail.BatteryFlyout? _batFlyout;
    Tail.ClockFlyout? _clockFlyout;
    Tail.BinFlyout? _binFlyout;
    Shots.ShotsFlyout? _shotsFlyout;
    Shots.ShotPeek? _shotPeek;
    Clips.ClipsFlyout? _clipsFlyout;
    Charge.ChargeFlyout? _chargeFlyout;

    public MenuPopup Menu => _menu ??= new MenuPopup();
    public SoundFlyout SoundFlyout => _sound ??= new SoundFlyout(Audio, Icons, Config);
    public WindowListPopup WindowList => _list ??= new WindowListPopup(Tracker, Icons);
    public Tail.MediaFlyout MediaFlyout => _mediaFlyout ??= new Tail.MediaFlyout(Media);
    public Start.StartMenu StartMenu => _start ??= new Start.StartMenu(this, Catalog);
    public Tray.TrayFlyout TrayFlyout => _trayFlyout ??= new Tray.TrayFlyout(Tray);
    public Tail.NetworkFlyout NetworkFlyout => _netFlyout ??= new Tail.NetworkFlyout(Network);
    public Tail.BatteryFlyout BatteryFlyout => _batFlyout ??= new Tail.BatteryFlyout(Battery);
    public Tail.ClockFlyout ClockFlyout => _clockFlyout ??= new Tail.ClockFlyout();
    public Tail.BinFlyout BinFlyout => _binFlyout ??= new Tail.BinFlyout(Bin, Icons);
    public Shots.ShotsFlyout ShotsFlyout => _shotsFlyout ??= new Shots.ShotsFlyout(Shots);
    public Shots.ShotPeek ShotPeek => _shotPeek ??= new Shots.ShotPeek();
    public Clips.ClipsFlyout ClipsFlyout => _clipsFlyout ??= new Clips.ClipsFlyout(Clips);
    public Charge.ChargeFlyout ChargeFlyout => _chargeFlyout ??= new Charge.ChargeFlyout(Charge) { OpenSettings = () => OpenSettings("widgets") };

    /// На ⌃ точка: программа из трея что-то сообщила, а окошко трея ещё не открывали.
    public bool TrayAttention { get; set; }

    /// Вернуть панель задач Windows до перезапуска оболочки (меню трея, Ctrl+Alt+Shift+T).
    public Action RestoreTaskbar { get; set; } = () => { };

    public List<IDropZone> DropZones { get; } = [];

    public AudioSettings AudioSettings => new(Config.Widget("audio"));

    /// Открыть или закрыть Пуск над кнопкой этого дока (null — док монитора с курсором).
    public Action<Dock.Dock?> ToggleStart { get; set; } = _ => { };

    /// Открыть или закрыть служебное меню (Win+X) над кнопкой Пуска этого дока.
    public Action<Dock.Dock?> ToggleTools { get; set; } = _ => { };

    /// Программу запустили не из дока (из Пуска): её иконка в доке пульсирует.
    public event Action<string>? Launched;
    public void NoteLaunch(string target)
    {
        Catalog.New.Opened(target);
        Launched?.Invoke(target);
    }

    /// Открыть окно настроек на странице (null — на последней открытой): меню трея, правый клик
    /// по Пуску, поиск в Пуске.
    public Action<string?> OpenSettings { get; set; } = _ => { };

    /// Перезапустить оболочку (кнопка в окне настроек).
    public Action Restart { get; set; } = () => { };

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
