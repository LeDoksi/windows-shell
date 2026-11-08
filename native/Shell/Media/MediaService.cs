using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WindowsShell.Media;

/// Что сейчас играет.
public sealed record NowPlaying(string AppId, string Title, string Artist, bool Playing, bool CanPlayPause, bool CanNext, bool CanPrevious);

/// Плеер с медиасессией (для выбора, когда их несколько — П23).
public sealed record MediaSource(string AppId, string Name);

/// Где трек сейчас: позиция на момент At, длительность, можно ли перематывать.
public sealed record MediaTimeline(TimeSpan Position, TimeSpan Duration, bool CanSeek, DateTimeOffset At, bool Playing)
{
    /// Позиция сейчас (пока играет, время идёт само).
    public TimeSpan Now
    {
        get
        {
            var p = Playing ? Position + (DateTimeOffset.Now - At) : Position;
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            return Duration > TimeSpan.Zero && p > Duration ? Duration : p;
        }
    }
}

/// «Сейчас играет» (как `media.rs`): музыка и видео, которые Windows показывает в своей плашке
/// громкости (GlobalSystemMediaTransportControlsSessionManager). Включается вместе с виджетом.
public sealed class MediaService(DispatcherQueue queue)
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    bool _running;
    int _version;
    string? _chosen;
    int _coverTries;
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _coverRetry;

    public NowPlaying? Current { get; private set; }
    public BitmapImage? Cover { get; private set; }

    /// Все плееры с сессией (выбор показываем, только если их больше одного).
    public IReadOnlyList<MediaSource> Sessions { get; private set; } = [];

    public MediaTimeline? Timeline { get; private set; }

    /// Позиция или длительность поменялись (поток интерфейса).
    public event Action? TimelineChanged;

    /// Трек, пауза или обложка поменялись. В потоке интерфейса.
    public event Action? Changed;

    public async void Start()
    {
        if (_running) return;
        _running = true;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (!_running) return;
            _manager.CurrentSessionChanged += (_, _) => queue.TryEnqueue(Bind);
            _manager.SessionsChanged += (_, _) => queue.TryEnqueue(Bind);
            Bind();
        }
        catch (Exception e)
        {
            Log.Error("медиа", e);
        }
    }

    public void Stop()
    {
        _running = false;
        Unbind();
        _manager = null;
        Current = null;
        Cover = null;
        Changed?.Invoke();
    }

    void Bind()
    {
        Unbind();
        var all = _manager?.GetSessions().ToList() ?? [];
        Sessions = all.Select(x => new MediaSource(x.SourceAppUserModelId ?? "", SourceName(x.SourceAppUserModelId))).ToList();
        // Выбранный вручную плеер, пока он есть; иначе тот, что Windows считает текущим.
        _session = all.FirstOrDefault(x => _chosen != null && x.SourceAppUserModelId == _chosen) ?? _manager?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionEvent;
            _session.PlaybackInfoChanged += OnSessionEvent;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        _ = Refresh();
    }

    void Unbind()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnSessionEvent;
        _session.PlaybackInfoChanged -= OnSessionEvent;
        _session.TimelinePropertiesChanged -= OnTimeline;
        _session = null;
    }

    void OnSessionEvent(GlobalSystemMediaTransportControlsSession s, object e) => queue.TryEnqueue(() => _ = Refresh());

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, object e) => queue.TryEnqueue(ReadTimeline);

    void ReadTimeline()
    {
        var s = _session;
        if (s is null) { Timeline = null; TimelineChanged?.Invoke(); return; }
        try
        {
            var t = s.GetTimelineProperties();
            var info = s.GetPlaybackInfo();
            var duration = t.EndTime - t.StartTime;
            Timeline = new MediaTimeline(t.Position - t.StartTime, duration, info.Controls.IsPlaybackPositionEnabled, t.LastUpdatedTime,
                info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        }
        catch { Timeline = null; }
        TimelineChanged?.Invoke();
    }

    /// Выбрать плеер (переключатель в острове при нескольких сессиях).
    public void Select(string appId)
    {
        _chosen = appId;
        Bind();
    }

    public async void Seek(TimeSpan position)
    {
        try
        {
            if (_session is null) return;
            var start = _session.GetTimelineProperties().StartTime;
            await _session.TryChangePlaybackPositionAsync((start + position).Ticks);
        }
        catch (Exception e) { Log.Error("медиа: перемотка", e); }
    }

    /// Понятное имя плеера по его AUMID: «Яндекс Музыка», «Chrome», «Spotify».
    public static string SourceName(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "Плеер";
        try
        {
            if (aumid.Contains('!'))
            {
                var name = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid)?.DisplayInfo?.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch { }
        var id = aumid;
        if (id.Contains('!')) id = id[..id.IndexOf('!')];
        if (id.Contains('_')) id = id[..id.IndexOf('_')];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id[..^4];
        id = id.Replace('\\', '/');
        if (id.Contains('/')) id = id[(id.LastIndexOf('/') + 1)..];
        var parts = id.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // «A025C540.Yandex.Music» → «Yandex Music»: первая часть пакета — код издателя.
        if (parts.Length > 1 && parts[0].Length >= 6 && parts[0].All(char.IsLetterOrDigit) && parts[0].Any(char.IsDigit)) parts = parts[1..];
        var text = string.Join(' ', parts);
        return text switch
        {
            "msedge" => "Edge",
            "chrome" => "Chrome",
            "firefox" => "Firefox",
            "" => "Плеер",
            _ => char.ToUpper(text[0]) + text[1..],
        };
    }

    async Task Refresh()
    {
        var version = ++_version;
        var s = _session;
        if (s is null)
        {
            if (Current != null) { Current = null; Cover = null; Changed?.Invoke(); }
            return;
        }
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (version != _version) return;
            var info = s.GetPlaybackInfo();
            var controls = info.Controls;
            var now = new NowPlaying(
                s.SourceAppUserModelId ?? "",
                props?.Title ?? "",
                props?.Artist ?? "",
                info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                controls.IsNextEnabled,
                controls.IsPreviousEnabled);
            var trackChanged = Current is null || Current.Title != now.Title || Current.Artist != now.Artist || Current.AppId != now.AppId;
            if (string.IsNullOrEmpty(now.Title)) now = null;
            ReadTimeline();
            // И7: обложку перечитываем при любом изменении свойств, пока её нет — некоторые плееры
            // (отдельное приложение Яндекс Музыки) присылают картинку позже названия.
            var needCover = now != null && (trackChanged || Cover is null);
            if (now == Current && !needCover) return;
            Current = now;
            if (now is null) { Cover = null; _coverTries = 0; }
            else if (needCover)
            {
                if (trackChanged) { Cover = null; _coverTries = 0; }
                Cover = await LoadCover(props?.Thumbnail, version);
                if (version != _version) return;
                if (Cover is null) RetryCover();
                else if (_coverTries > 0) Log.Write($"медиа: обложка пришла с попытки {_coverTries + 1}");
            }
            if (version != _version) return;
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("медиа: обновление", e);
        }
    }

    /// Пустая обложка: ещё две попытки — через 1,5 и 4 с.
    void RetryCover()
    {
        if (_coverTries >= 2) return;
        var delay = _coverTries == 0 ? 1500 : 2500;
        _coverTries++;
        _coverRetry ??= queue.CreateTimer();
        _coverRetry.IsRepeating = false;
        _coverRetry.Interval = TimeSpan.FromMilliseconds(delay);
        _coverRetry.Tick -= OnCoverRetry;
        _coverRetry.Tick += OnCoverRetry;
        _coverRetry.Start();
    }

    void OnCoverRetry(Microsoft.UI.Dispatching.DispatcherQueueTimer t, object o) { if (Cover is null && Current != null) _ = Refresh(); }

    async Task<BitmapImage?> LoadCover(IRandomAccessStreamReference? thumb, int version)
    {
        if (thumb is null) return null;
        try
        {
            using var stream = await thumb.OpenReadAsync();
            if (version != _version || stream.Size > 4 * 1024 * 1024) return null;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            return bmp;
        }
        catch { return null; }
    }

    public async void PlayPause()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }
}
