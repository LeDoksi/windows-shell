using System.Diagnostics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Shots;

/// Запись экрана в MP4 (design-v2 п. 4). Кадры даёт захват экрана Windows (с курсором), из
/// кадра монитора вырезается выбранная область; кодирует в H.264 + AAC сама Windows
/// (MediaTranscoder, видеокарта, если умеет). Ровно 30 кадров в секунду: если экран не
/// меняется, повторяется последний кадр. Пауза останавливает часы записи — в файле её не видно.
public sealed class Recorder
{
    const int Fps = 30;
    static readonly TimeSpan FrameTime = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / Fps);
    static readonly TimeSpan AudioChunk = TimeSpan.FromMilliseconds(20);
    const int AudioFrames = AudioMix.Rate / 50;

    readonly Gpu _gpu;
    readonly RECT _region; // относительно монитора
    readonly int _w, _h;
    readonly string _path;
    readonly AudioMix _audio;
    readonly IntPtr _latest;
    readonly Stack<(IntPtr Tex, IDirect3DSurface Surface)> _free = new();
    readonly List<(IntPtr Tex, IDirect3DSurface Surface)> _all = [];
    readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Stopwatch _clock = new();
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    IRandomAccessStream? _file;
    Task? _transcode;
    long _videoFrames, _audioFrames;
    TimeSpan _pausedTotal;
    long _pausedAt = -1;
    volatile bool _stopping;

    /// Запись оборвалась сама (кодировщик отказал).
    public event Action<string>? Failed;

    Recorder(Gpu gpu, RECT region, string path, bool system, bool mic)
    {
        _gpu = gpu;
        _region = region;
        _w = region.Width;
        _h = region.Height;
        _path = path;
        _latest = gpu.CreateTexture(_w, _h);
        _audio = new AudioMix(system, mic);
    }

    public string Path => _path;

    /// Сколько записано (без пауз).
    public TimeSpan Elapsed
    {
        get
        {
            lock (_clock)
            {
                var paused = _pausedAt >= 0 ? Stopwatch.GetElapsedTime(_pausedAt) : TimeSpan.Zero;
                var t = _clock.Elapsed - _pausedTotal - paused;
                return t < TimeSpan.Zero ? TimeSpan.Zero : t;
            }
        }
    }

    public bool Paused
    {
        get => _pausedAt >= 0;
        set
        {
            lock (_clock)
            {
                if (value == Paused) return;
                if (value) _pausedAt = Stopwatch.GetTimestamp();
                else
                {
                    _pausedTotal += Stopwatch.GetElapsedTime(_pausedAt);
                    _pausedAt = -1;
                }
            }
            _audio.Paused = value;
            Log.Write(value ? "запись: пауза" : "запись: дальше");
        }
    }

    public bool Mic { get => _audio.MicOn; set => _audio.MicOn = value; }
    public bool SystemSound { get => _audio.SystemOn; set => _audio.SystemOn = value; }

    /// Начать запись области `region` (координаты экрана) монитора `monitor`.
    public static async Task<Recorder> Start(IntPtr monitor, RECT monitorBounds, RECT region, string path, bool system, bool mic)
    {
        var local = new RECT { Left = region.Left - monitorBounds.Left, Top = region.Top - monitorBounds.Top, Right = region.Right - monitorBounds.Left, Bottom = region.Bottom - monitorBounds.Top };
        var gpu = new Gpu();
        Recorder? r = null;
        try
        {
            r = new Recorder(gpu, local, path, system, mic);
            await r.Begin(monitor);
            return r;
        }
        catch
        {
            if (r != null) await r.Stop();
            else gpu.Dispose();
            throw;
        }
    }

    async Task Begin(IntPtr monitor)
    {
        var item = Gpu.ItemForMonitor(monitor);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_gpu.WinRT, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = true;
        // Жёлтая рамка Windows вокруг записываемого экрана не нужна (Windows 11; на 10 свойства нет).
        try { _session.IsBorderRequired = false; } catch { }
        _session.StartCapture();
        if (await Task.WhenAny(_firstFrame.Task, Task.Delay(2000)) != _firstFrame.Task)
            throw new InvalidOperationException("захват экрана не дал ни одного кадра");

        // Не все кодировщики принимают любой набор (на mephPC: MF_E_TRANSFORM_TYPE_NOT_SET):
        // пробуем по очереди, от лучшего к простому. Без звука — только если со звуком никак.
        PrepareTranscodeResult? prepared = null;
        foreach (var (sound, hardware, template) in Variants)
        {
            try
            {
                prepared = await Prepare(sound, hardware, template);
                Variant = $"{(sound ? "со звуком" : "без звука")}, {(hardware ? "видеокарта" : "процессор")}, {(template ? "профиль Windows" : "свой профиль")}";
                HasSound = sound;
                if (!sound) { _audio.SystemOn = false; _audio.MicOn = false; }
                break;
            }
            catch (Exception e)
            {
                var line = $"{(sound ? "звук" : "без звука")}/{(hardware ? "видеокарта" : "процессор")}/{(template ? "профиль Windows" : "свой профиль")}: 0x{e.HResult:X8} {e.Message.Trim()}";
                Attempts.Add(line);
                Log.Write($"запись: кодировщик не принял {line}");
            }
        }
        if (prepared is null) throw new InvalidOperationException("кодировщик видео не принял ни один вариант (подробности в журнале)");
        _audio.Reset();
        _clock.Start();
        _transcode = prepared.TranscodeAsync().AsTask();
        _ = _transcode.ContinueWith(t =>
        {
            if (t.IsFaulted && !_stopping) Failed?.Invoke(t.Exception?.InnerException?.Message ?? "ошибка кодировщика");
        }, TaskScheduler.Default);
        Log.Write($"запись: {_w}×{_h}, {Fps} кадров/с, {Bitrate / 1000} кбит/с, {Variant} → {System.IO.Path.GetFileName(_path)}");
    }

    static readonly (bool Sound, bool Hardware, bool Template)[] Variants =
        [(true, true, false), (true, true, true), (true, false, false), (true, false, true), (false, true, false), (false, false, false)];

    /// Как в итоге пишется (для журнала и проверки).
    public string Variant { get; private set; } = "";
    /// Варианты, которые кодировщик не принял.
    public List<string> Attempts { get; } = [];
    /// В файле есть звук.
    public bool HasSound { get; private set; }

    /// ~0,15 бита на пиксель: текст на экране остаётся чётким.
    uint Bitrate => (uint)Math.Clamp((long)_w * _h * Fps * 15 / 100, 2_000_000, 40_000_000);

    async Task<PrepareTranscodeResult> Prepare(bool sound, bool hardware, bool template)
    {
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_w, (uint)_h);
        input.FrameRate.Numerator = Fps;
        input.FrameRate.Denominator = 1;
        input.PixelAspectRatio.Numerator = 1;
        input.PixelAspectRatio.Denominator = 1;
        var video = new VideoStreamDescriptor(input);
        var source = sound
            ? new MediaStreamSource(video, new AudioStreamDescriptor(AudioEncodingProperties.CreatePcm(AudioMix.Rate, 2, 16)))
            : new MediaStreamSource(video);
        source.BufferTime = TimeSpan.Zero;
        source.CanSeek = false;
        source.Starting += (_, a) => a.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += OnSampleRequested;

        var profile = template
            ? MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p)
            : new MediaEncodingProfile
            {
                Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 },
                Video = VideoEncodingProperties.CreateH264(),
                Audio = AudioEncodingProperties.CreateAac(AudioMix.Rate, 2, 192_000),
            };
        profile.Video.Width = (uint)_w;
        profile.Video.Height = (uint)_h;
        profile.Video.FrameRate.Numerator = Fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = Bitrate;
        if (!sound) profile.Audio = null;

        _file?.Dispose();
        _file = await FileRandomAccessStream.OpenAsync(_path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = hardware };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, _file, profile);
        if (!prepared.CanTranscode) throw new InvalidOperationException($"кодировщик не готов: {prepared.FailureReason}");
        return prepared;
    }

    /// Новый кадр монитора: вырезаем свою область в «последний кадр».
    void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || _stopping) return;
            var size = frame.ContentSize;
            var right = Math.Min(_region.Right, size.Width);
            var bottom = Math.Min(_region.Bottom, size.Height);
            if (right <= _region.Left || bottom <= _region.Top) return;
            var tex = Gpu.Texture(frame.Surface);
            try { _gpu.CopyRegion(_latest, tex, _region.Left, _region.Top, right, bottom); }
            finally { System.Runtime.InteropServices.Marshal.Release(tex); }
            _firstFrame.TrySetResult();
        }
        catch (Exception e)
        {
            Log.Error("запись: кадр", e);
        }
    }

    void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        var video = request.StreamDescriptor is VideoStreamDescriptor;
        _ = Task.Run(async () =>
        {
            try { request.Sample = video ? await NextVideo() : await NextAudio(); }
            catch (Exception e) { Log.Error("запись: кусок", e); request.Sample = null; }
            finally { deferral.Complete(); }
        });
    }

    /// Ждать, пока часы записи дойдут до `t` (на паузе стоят). false — запись остановили.
    async Task<bool> WaitFor(TimeSpan t)
    {
        while (!_stopping)
        {
            var left = t - Elapsed;
            if (left <= TimeSpan.Zero) return true;
            await Task.Delay(Math.Clamp((int)left.TotalMilliseconds, 1, 50));
        }
        return false;
    }

    async Task<MediaStreamSample?> NextVideo()
    {
        var ts = FrameTime * _videoFrames;
        if (!await WaitFor(ts)) return null;
        (IntPtr Tex, IDirect3DSurface Surface) slot;
        lock (_free)
        {
            if (!_free.TryPop(out slot))
            {
                var tex = _gpu.CreateTexture(_w, _h);
                slot = (tex, Gpu.Surface(tex));
                _all.Add(slot);
            }
        }
        _gpu.Copy(slot.Tex, _latest);
        var sample = MediaStreamSample.CreateFromDirect3D11Surface(slot.Surface, ts);
        sample.Duration = FrameTime;
        // Кодировщик закончил с кадром — текстура снова свободна.
        sample.Processed += (_, _) => { lock (_free) _free.Push(slot); };
        _videoFrames++;
        return sample;
    }

    async Task<MediaStreamSample?> NextAudio()
    {
        var ts = TimeSpan.FromTicks(_audioFrames * TimeSpan.TicksPerSecond / AudioMix.Rate);
        // Отдаём кусок, когда он уже прозвучал.
        if (!await WaitFor(ts + AudioChunk)) return null;
        var bytes = _audio.Read(AudioFrames);
        var sample = MediaStreamSample.CreateFromBuffer(CryptographicBuffer.CreateFromByteArray(bytes), ts);
        sample.Duration = AudioChunk;
        _audioFrames += AudioFrames;
        return sample;
    }

    /// Остановить и дописать файл. true — файл готов.
    public async Task<bool> Stop()
    {
        _stopping = true;
        var length = Elapsed;
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        _audio.Dispose();
        var ok = false;
        if (_transcode != null)
        {
            if (await Task.WhenAny(_transcode, Task.Delay(15_000)) == _transcode) ok = _transcode.IsCompletedSuccessfully;
            else Log.Write("запись: кодировщик не закончил за 15 с");
        }
        _file?.Dispose();
        // Кадры отпускаем, когда кодировщик закончил.
        lock (_free)
        {
            foreach (var (tex, surface) in _all)
            {
                (surface as IDisposable)?.Dispose();
                System.Runtime.InteropServices.Marshal.Release(tex);
            }
            _all.Clear();
            _free.Clear();
        }
        System.Runtime.InteropServices.Marshal.Release(_latest);
        _gpu.Dispose();
        ok = ok && File.Exists(_path) && new FileInfo(_path).Length > 0;
        Log.Write(ok ? $"запись готова: {System.IO.Path.GetFileName(_path)}, {length:mm\\:ss}" : "запись: файл не получился");
        if (!ok) try { File.Delete(_path); } catch { }
        return ok;
    }
}
