using System.Text.Json;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using WindowsShell.Config;
using WindowsShell.Native;

namespace WindowsShell.Charge;

/// Виджет «Заряд устройств» (design-v2 п. 7): сам находит устройства с батареей (Bluetooth из
/// Windows, Logitech напрямую по HID++ — G HUB не нужен), показывает значок в хвосте, когда что-то садится, и предупреждает:
/// уведомление Windows при пороге (20 %) и при 10 %, повтор — только после зарядки выше 30 %.
/// В полноэкранной игре Windows уведомления прячет, поэтому там ещё мягкий сигнал в наушники
/// и своя плашка в углу на 5 секунд.
public sealed class ChargeService
{
    /// Второе предупреждение — всегда при 10 %.
    public const int Second = 10;

    readonly DispatcherQueue _queue;
    readonly ShellConfig _config;
    readonly DispatcherQueueTimer _poll, _soon;
    readonly string _stateFile = Path.Combine(ShellConfig.Dir, "charge-alerts.json");
    /// Сколько предупреждений уже было по устройству: 1 — при пороге, 2 — при 10 %.
    Dictionary<string, int> _alerted = [];
    List<ChargeDevice> _found = [];
    bool _reading, _started;
    ChargeBanner? _banner;
    MediaPlayer? _player;

    public IReadOnlyList<ChargeDevice> Devices { get; private set; } = [];

    /// Список или уровни поменялись (поток интерфейса).
    public event Action? Changed;

    /// Монитор, где сейчас полноэкранная игра или видео (null — нет такого).
    public Func<Screen?> GameScreen { get; set; } = () => null;

    public ChargeService(DispatcherQueue queue, ShellConfig config, MessageWindow messages)
    {
        _queue = queue;
        _config = config;
        _alerted = LoadState();
        _poll = queue.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(30);
        _poll.Tick += (_, _) => Refresh();
        // Устройство подключили или отключили — Windows шлёт WM_DEVICECHANGE; пусть уляжется.
        _soon = queue.CreateTimer();
        _soon.Interval = TimeSpan.FromSeconds(3);
        _soon.IsRepeating = false;
        _soon.Tick += (_, _) => Refresh();
        messages.Message += (msg, _, _) => { if (msg == 0x0219 && Enabled) _soon.Start(); };
        messages.Resumed += () => { if (Enabled) _soon.Start(); };
        AirPods.Changed += () => _queue.TryEnqueue(() => { if (Enabled && !_soon.IsRunning) _soon.Start(); });
    }

    public ChargeSettings Settings => new(_config.Widget("charge"));

    /// «Никогда» в настройках виджета — ничего не читаем и не предупреждаем.
    public bool Enabled => _config.TempWidgetMode("charge") != ShellConfig.ShowNever;

    public int Threshold => Settings.Threshold;

    /// Устройства без «не следить».
    public IEnumerable<ChargeDevice> Watched
    {
        get
        {
            var ignored = Settings.Ignored;
            return Devices.Where(d => !ignored.ContainsKey(d.Key));
        }
    }

    /// Севшие: подключены, не заряжаются, уровень не выше порога.
    public List<ChargeDevice> Low => Watched.Where(IsLow).ToList();

    public bool IsLow(ChargeDevice d) => d.Connected && !d.Charging && d.Percent is { } p && p <= Threshold;

    /// Включить или выключить по настройке (зовётся при каждой смене конфига).
    public void Apply()
    {
        if (Enabled == _started) return;
        _started = Enabled;
        if (_started)
        {
            _poll.Start();
            Refresh();
        }
        else
        {
            _poll.Stop();
            Devices = [];
            Changed?.Invoke();
        }
    }

    /// Перечитать устройства в фоне (окошко открыли — тоже зовём, чтобы цифры были свежие).
    public void Refresh()
    {
        if (!_started || _reading) return;
        _reading = true;
        Task.Run(async () =>
        {
            var list = new List<ChargeDevice>();
            try { list.AddRange(await Bluetooth.Read()); }
            catch (Exception e) { Log.Error("заряд: Bluetooth", e); }
            try { list.AddRange(Logitech.Read()); }
            catch (Exception e) { Log.Error("заряд: Logitech", e); }
            _queue.TryEnqueue(() =>
            {
                _reading = false;
                _found = list;
                Merge();
            });
        });
    }

    void Merge()
    {
        if (!_started) return;
        var all = _found
            // Не подключено — уровень старый, не показываем.
            .Select(d => d.Connected ? d : d with { Percent = null, Charging = false, Detail = null })
            .GroupBy(d => d.Key).Select(g => g.First())
            .OrderBy(d => d.Connected ? 0 : 1).ThenBy(d => d.Percent ?? 101).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var changed = all.Count != Devices.Count || all.Zip(Devices).Any(p => p.First != p.Second);
        if (changed)
        {
            foreach (var d in all.Where(d => !Devices.Any(o => o.Key == d.Key && o.Percent == d.Percent && o.Connected == d.Connected && o.Charging == d.Charging)))
                Log.Write($"заряд: {d.Name} ({d.Source}) — {(d.Connected ? d.Percent is { } p ? $"{p} %" : "?" : "не подключено")}{(d.Charging ? ", заряжается" : "")}");
            Devices = all;
        }
        Check();
        if (changed) Changed?.Invoke();
    }

    // ---------- предупреждения ----------

    void Check()
    {
        var threshold = Threshold;
        // Снова предупредим, только когда зарядили заметно выше порога (по умолчанию выше 30 %).
        var rearm = Math.Max(30, threshold + 10);
        var dirty = false;
        foreach (var d in Watched)
        {
            if (!d.Connected || d.Percent is not { } p) continue;
            var stage = _alerted.GetValueOrDefault(d.Key);
            if (p > rearm)
            {
                if (stage != 0) { _alerted.Remove(d.Key); dirty = true; }
                continue;
            }
            if (d.Charging) continue;
            var next = p <= Second ? 2 : p <= threshold ? 1 : 0;
            // Порог 10 % и ниже — предупреждение одно.
            if (threshold <= Second && next > 0) next = 2;
            if (next <= stage) continue;
            _alerted[d.Key] = next;
            dirty = true;
            Alert(d, p, second: next == 2 && threshold > Second);
        }
        if (dirty) SaveState();
    }

    void Alert(ChargeDevice d, int percent, bool second)
    {
        var title = $"{d.Title}: {percent} %";
        var text = second ? "Совсем мало, пора зарядить" : "Пора зарядить";
        var game = GameScreen();
        Log.Write($"заряд: предупреждение «{title}»{(game != null ? " (идёт игра)" : "")}");
        // В игре Windows уведомление не покажет (оно тихо уйдёт в центр уведомлений): свой сигнал и плашка.
        Toasts.Show(title, text, silent: game != null);
        if (game is null) return;
        if (Settings.GameSound) Play(second);
        (_banner ??= new ChargeBanner(_queue)).Show(d, percent, text, game);
    }

    /// Мягкий сигнал (две ноты вниз; для 10 % свой) в устройство вывода по умолчанию — в игре это наушники.
    public void Play(bool second)
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Sounds", second ? "charge-10.mp3" : "charge-20.mp3");
            if (!File.Exists(file)) { Log.Write($"заряд: нет звука {file}"); return; }
            _player?.Dispose();
            _player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(file)), AudioCategory = MediaPlayerAudioCategory.Alerts };
            _player.Play();
        }
        catch (Exception e)
        {
            Log.Error("заряд: сигнал", e);
        }
    }

    /// Показать, как выглядит предупреждение в игре (кнопка в настройках).
    public void Preview()
    {
        var d = Watched.FirstOrDefault(x => x.Percent != null) ?? new ChargeDevice("preview", "RK-S98", DeviceKind.Keyboard, 18, false, true, "");
        var screen = GameScreen();
        if (Settings.GameSound) Play(second: false);
        (_banner ??= new ChargeBanner(_queue)).Show(d, d.Percent ?? 18, "Пора зарядить", screen);
    }

    public void Ignore(ChargeDevice d)
    {
        _config.Update(_ => Settings.Ignore(d.Key, d.Name));
        Changed?.Invoke();
    }

    public void Unignore(string key)
    {
        _config.Update(_ => Settings.Unignore(key));
        Changed?.Invoke();
    }

    Dictionary<string, int> LoadState()
    {
        try
        {
            if (File.Exists(_stateFile)) return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_stateFile)) ?? [];
        }
        catch (Exception e)
        {
            Log.Error("заряд: память предупреждений", e);
        }
        return [];
    }

    void SaveState()
    {
        try
        {
            Directory.CreateDirectory(ShellConfig.Dir);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(_alerted));
        }
        catch (Exception e)
        {
            Log.Error("заряд: память предупреждений", e);
        }
    }
}
