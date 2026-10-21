using System.Drawing.Imaging;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using WindowsShell.Apps;
using WindowsShell.Config;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;
using D = System.Drawing;

namespace WindowsShell.Shots;

/// Виджет «Снимки» (design-v2 п. 4, замена ShareX): PrtSc — снимок, Shift+PrtSc — запись
/// (повторно — стоп), история за 3 дня, миниатюра в углу экрана, редактор.
public sealed class ShotsService
{
    readonly DispatcherQueue _queue;
    readonly ShellConfig _config;
    readonly Monitors _monitors;
    readonly Tail.RecycleBin _bin;
    readonly DispatcherQueueTimer _cleanup, _tick;
    ShotThumb? _thumb;
    Recorder? _rec;
    RecordFrame? _frame;
    List<ShotItem> _items = [];
    bool _busy;
    int _refresh;

    /// История или запись поменялись (значок в хвосте, окошко).
    public event Action? Changed;

    /// Раз в секунду, пока идёт запись (таймер в хвосте и на панели).
    public event Action? Tick;

    /// Где значок «Снимки» в хвосте (физические пиксели, как будто док виден). null — значка нет.
    public Func<Rect?> Anchor { get; set; } = () => null;

    public ShotsService(DispatcherQueue queue, ShellConfig config, Monitors monitors, Tail.RecycleBin bin, MessageWindow messages)
    {
        _queue = queue;
        _config = config;
        _monitors = monitors;
        _bin = bin;
        Clip.Owner = messages.Hwnd;
        _cleanup = queue.CreateTimer();
        _cleanup.Interval = TimeSpan.FromHours(1);
        _cleanup.Tick += (_, _) => Refresh(cleanup: true);
        _cleanup.Start();
        _tick = queue.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => { _frame?.Update(); Tick?.Invoke(); };
        Refresh(cleanup: true);
    }

    public ShotsSettings Settings => new(_config.Widget("shots"));

    /// PrtSc и Shift+PrtSc наши (выключено — как в Windows).
    public bool Enabled => _config.WidgetEnabled("shots");

    public IReadOnlyList<ShotItem> Items => _items;

    public bool Recording => _rec != null;
    public TimeSpan RecordingTime => _rec?.Elapsed ?? TimeSpan.Zero;
    public bool RecordingPaused => _rec?.Paused ?? false;
    public bool RecordMic => _rec?.Mic ?? Settings.Mic;
    public bool RecordSystemSound => _rec?.SystemSound ?? Settings.SystemSound;

    /// Перечитать историю (в фоне); `cleanup` — сначала удалить старше 3 дней.
    public void Refresh(bool cleanup = false)
    {
        var v = ++_refresh;
        Task.Run(() =>
        {
            if (cleanup) ShotFiles.Cleanup();
            var list = ShotFiles.List();
            _queue.TryEnqueue(() =>
            {
                if (v != _refresh) return;
                _items = list;
                Changed?.Invoke();
            });
        });
    }

    void Add(ShotItem item)
    {
        _items = [item, .. _items.Where(i => i.Path != item.Path)];
        Changed?.Invoke();
    }

    // ---------- снимок ----------

    /// PrtSc: экран замирает, выбираешь область, окно или монитор.
    public async void Snap()
    {
        if (_busy || Selector.IsOpen) return;
        _busy = true;
        try
        {
            _thumb?.Hide();
            Selection? sel;
            D.Bitmap bmp;
            using (var frozen = Frozen.Capture())
            {
                sel = await Selector.Pick(frozen, _monitors.All, record: false);
                if (sel is null) return;
                bmp = frozen.Crop(sel.Rect);
            }
            using (bmp)
            {
                var path = ShotFiles.NewPath(ShotKind.Image);
                using var png = new MemoryStream();
                bmp.Save(png, ImageFormat.Png);
                var bytes = png.ToArray();
                Clip.SetImage(bmp, bytes);
                await File.WriteAllBytesAsync(path, bytes);
                var item = ShotFiles.Parse(path) ?? new ShotItem(path, DateTime.Now, ShotKind.Image);
                Log.Write($"снимок {(sel.Window != IntPtr.Zero ? "окна" : sel.Monitor ? "монитора" : "области")} {bmp.Width}×{bmp.Height} → {item.Name}");
                Add(item);
                ShowThumb(item, $"Снимок · {bmp.Width} × {bmp.Height}");
            }
        }
        catch (Exception e)
        {
            Log.Error("снимок", e);
        }
        finally
        {
            _busy = false;
        }
    }

    void ShowThumb(ShotItem item, string caption)
    {
        if (_thumb is null)
        {
            _thumb = new ShotThumb(_queue);
            _thumb.Clicked += Edit;
        }
        _thumb.Show(item, caption, Anchor() ?? FallbackAnchor());
    }

    /// Значка нет (выключен или хвост на другом мониторе): справа внизу основного монитора.
    Rect FallbackAnchor()
    {
        var s = _monitors.Primary;
        return new Rect(s.Bounds.Right - 160 * s.Scale, s.Bounds.Bottom - 60 * s.Scale, 36 * s.Scale, 40 * s.Scale);
    }

    // ---------- запись ----------

    /// Shift+PrtSc: выбрать область и сразу писать; во время записи — стоп.
    public async void Record()
    {
        if (_rec != null) { StopRecording(); return; }
        if (_busy || Selector.IsOpen) return;
        _busy = true;
        try
        {
            _thumb?.Hide();
            Selection? sel;
            using (var frozen = Frozen.Capture()) sel = await Selector.Pick(frozen, _monitors.All, record: true);
            if (sel is null) return;
            // Запись — в пределах одного монитора (того, где середина рамки); размер чётный.
            var cx = (sel.Rect.Left + sel.Rect.Right) / 2;
            var cy = (sel.Rect.Top + sel.Rect.Bottom) / 2;
            var screen = _monitors.At(cx, cy);
            var r = Gdi.Intersect(sel.Rect, screen.Bounds);
            r.Right = r.Left + Math.Max(2, r.Width & ~1);
            r.Bottom = r.Top + Math.Max(2, r.Height & ~1);
            if (r.Right > screen.Bounds.Right) { r.Left -= 2; r.Right -= 2; }
            if (r.Bottom > screen.Bounds.Bottom) { r.Top -= 2; r.Bottom -= 2; }
            var monitor = MonitorFromPoint(new POINT { X = cx, Y = cy }, MONITOR_DEFAULTTONEAREST);
            var s = Settings;
            var path = ShotFiles.NewPath(ShotKind.Video);
            var rec = await Recorder.Start(monitor, screen.Bounds, r, path, s.SystemSound, s.Mic);
            rec.Failed += reason => _queue.TryEnqueue(() =>
            {
                if (_rec != rec) return;
                Log.Write($"запись оборвалась: {reason}");
                Toasts.Show("Запись экрана остановилась", reason, silent: true);
                StopRecording();
            });
            if (!rec.HasSound && (s.SystemSound || s.Mic))
                Toasts.Show("Запись идёт без звука", "Кодировщик Windows не принял звук. Подробности в журнале оболочки.", silent: true);
            _rec = rec;
            _frame = new RecordFrame(this, r, screen);
            _tick.Start();
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("запись: не началась", e);
            Toasts.Show("Запись экрана не началась", e.Message, silent: true);
        }
        finally
        {
            _busy = false;
        }
    }

    public async void StopRecording()
    {
        var rec = _rec;
        if (rec is null) return;
        _rec = null;
        _tick.Stop();
        _frame?.Close();
        _frame = null;
        Changed?.Invoke();
        var length = rec.Elapsed;
        var ok = await rec.Stop();
        if (!ok) { Toasts.Show("Запись экрана не сохранилась", "Подробности в журнале оболочки.", silent: true); return; }
        var item = ShotFiles.Parse(rec.Path) ?? new ShotItem(rec.Path, DateTime.Now, ShotKind.Video);
        Add(item);
        ShowThumb(item, $"Запись · {Length(length)}");
    }

    public static string Length(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public void TogglePause()
    {
        if (_rec is null) return;
        _rec.Paused = !_rec.Paused;
        _frame?.Update();
        Tick?.Invoke();
    }

    public void SetMic(bool on)
    {
        if (_rec != null) _rec.Mic = on;
        _config.Update(_ => Settings.Mic = on);
        _frame?.Update();
    }

    public void SetSystemSound(bool on)
    {
        if (_rec != null) _rec.SystemSound = on;
        _config.Update(_ => Settings.SystemSound = on);
        _frame?.Update();
    }

    // ---------- действия с историей ----------

    /// Клик по миниатюре или «Изменить»: снимок — редактор, запись — открыть в плеере.
    public void Edit(ShotItem item)
    {
        if (!File.Exists(item.Path)) { Refresh(); return; }
        if (item.IsImage) ShotEditor.Open(this, item);
        else Launcher.Open(item.Path);
    }

    public void Copy(ShotItem item)
    {
        try
        {
            if (!item.IsImage) { Clip.SetFiles([item.Path]); return; }
            using var stream = new MemoryStream(File.ReadAllBytes(item.Path));
            using var bmp = new D.Bitmap(stream);
            Clip.SetImage(bmp);
        }
        catch (Exception e)
        {
            Log.Error("снимки: копировать", e);
        }
    }

    public void Reveal(ShotItem item) => Launcher.Reveal(item.Path);

    /// В корзину (можно восстановить).
    public void Delete(ShotItem item)
    {
        if (_thumb?.Shows(item.Path) == true) _thumb.Hide();
        _bin.Delete([item.Path]);
        _items = _items.Where(i => i.Path != item.Path).ToList();
        Changed?.Invoke();
    }

    public async void SaveAs(ShotItem item, IntPtr owner)
    {
        try
        {
            var ext = Path.GetExtension(item.Path);
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(item.Path) };
            picker.FileTypeChoices.Add(ext.TrimStart('.').ToUpperInvariant(), [ext]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            File.Copy(item.Path, file.Path, overwrite: true);
            Log.Write($"снимки: сохранено как {file.Path}");
        }
        catch (Exception e)
        {
            Log.Error("снимки: сохранить как", e);
        }
    }

    /// Запись → GIF рядом (для коротких клипов).
    public async void SaveGif(ShotItem item)
    {
        try
        {
            var path = await Gif.FromVideo(item.Path);
            if (path is null) return;
            var gif = ShotFiles.Parse(path) ?? new ShotItem(path, item.Time, ShotKind.Gif);
            Add(gif);
            ShowThumb(gif, "GIF готов");
        }
        catch (Exception e)
        {
            Log.Error("снимки: GIF", e);
            Toasts.Show("GIF не получился", e.Message, silent: true);
        }
    }

    public static void OpenFolder() => Launcher.Run("explorer.exe", args: $"\"{ShotFiles.ShotsDir}\"");

    /// Редактор записал изменения в файл снимка: превью обновятся.
    public void NoteEdited(ShotItem item) => Changed?.Invoke();
}
