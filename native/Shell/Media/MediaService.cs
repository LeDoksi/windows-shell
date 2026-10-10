using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WindowsShell.Media;

/// Что сейчас играет.
public sealed record NowPlaying(string AppId, string Title, string Artist, bool Playing, bool CanPlayPause, bool CanNext, bool CanPrevious);

/// «Сейчас играет» (как `media.rs`): музыка и видео, которые Windows показывает в своей плашке
/// громкости (GlobalSystemMediaTransportControlsSessionManager). Включается вместе с виджетом.
public sealed class MediaService(DispatcherQueue queue)
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    bool _running;
    int _version;

    public NowPlaying? Current { get; private set; }
    public BitmapImage? Cover { get; private set; }

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
        _session = _manager?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnSessionEvent;
            _session.PlaybackInfoChanged += OnSessionEvent;
        }
        _ = Refresh();
    }

    void Unbind()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnSessionEvent;
        _session.PlaybackInfoChanged -= OnSessionEvent;
        _session = null;
    }

    void OnSessionEvent(GlobalSystemMediaTransportControlsSession s, object e) => queue.TryEnqueue(() => _ = Refresh());

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
            if (now == Current) return;
            Current = now;
            if (now is null) Cover = null;
            else if (trackChanged) Cover = await LoadCover(props?.Thumbnail, version);
            if (version != _version) return;
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("медиа: обновление", e);
        }
    }

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
