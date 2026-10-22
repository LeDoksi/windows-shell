using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace WindowsShell.Shots;

/// Перетащить файл из нашего окна в любую программу (чат, Проводник, браузер): своим
/// перетаскиванием WinUI (StartDragAsync), файл — StorageFile, как его отдаёт Проводник.
///
/// Раньше здесь был SHDoDragDrop прямо из PointerMoved. Его цикл OLE берёт кнопку мыши из
/// мышиных сообщений, а WinUI получает мышь указателем (WM_POINTER): цикл считал кнопку
/// отпущенной и бросал файл «в никуда» сразу после начала, обработчик тут же начинал снова —
/// 3–4 раза в секунду, а у окна под курсором оставалась картинка недоделанного перетаскивания.
/// В своём потоке цикл OLE не получает мышь вовсе и ждёт вечно. StartDragAsync ведёт
/// перетаскивание вводом самого WinUI и не держит поток интерфейса.
public static class FileDrag
{
    static bool _busy;

    /// Начать тащить файл из `element`; `point` — точка указателя из PointerMoved, кнопка ещё
    /// зажата. Сразу возвращается; `done(true)` — файл куда-то бросили. false — уже тащим.
    public static bool Start(UIElement element, PointerPoint point, string path, Action<bool>? done = null)
    {
        if (_busy) return false;
        _busy = true;
        var name = Path.GetFileName(path);
        async void OnStarting(UIElement sender, DragStartingEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                e.Data.SetStorageItems([file]);
                e.Data.RequestedOperation = DataPackageOperation.Copy;
                e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Link;
            }
            catch (Exception ex)
            {
                Log.Write($"снимки: перетащить {name} не получилось ({ex.Message})");
                e.Cancel = true;
            }
            finally { deferral.Complete(); }
        }
        element.DragStarting += OnStarting;
        try
        {
            var op = element.StartDragAsync(point);
            op.Completed = (o, status) =>
            {
                var result = DataPackageOperation.None;
                try { if (status == AsyncStatus.Completed) result = o.GetResults(); }
                catch { }
                var problem = status == AsyncStatus.Completed ? null : status.ToString();
                if (element.DispatcherQueue.HasThreadAccess) Finish(result, problem);
                else element.DispatcherQueue.TryEnqueue(() => Finish(result, problem));
            };
        }
        catch (Exception ex)
        {
            Log.Error("снимки: перетаскивание", ex);
            Finish(DataPackageOperation.None, "ошибка");
        }
        return true;

        void Finish(DataPackageOperation result, string? problem)
        {
            if (!_busy) return;
            _busy = false;
            element.DragStarting -= OnStarting;
            var dropped = result != DataPackageOperation.None;
            Log.Write($"снимки: перетащили {name} → {(dropped ? $"брошен ({result})" : problem ?? "не брошен")}");
            done?.Invoke(dropped);
        }
    }
}

/// Картинки для миниатюр: снимок читаем целиком в память (файл не держим открытым — его можно
/// сразу удалить или перезаписать), у видео берём кадр, который показывает Проводник.
public static class ShotImages
{
    public static async Task<ImageSource?> Load(ShotItem item, int px)
    {
        try
        {
            if (item.IsImage)
            {
                var bytes = await File.ReadAllBytesAsync(item.Path);
                using var mem = new InMemoryRandomAccessStream();
                using (var w = new DataWriter(mem.GetOutputStreamAt(0)))
                {
                    w.WriteBytes(bytes);
                    await w.StoreAsync();
                    await w.FlushAsync();
                    w.DetachStream();
                }
                mem.Seek(0);
                var bmp = new BitmapImage { DecodePixelWidth = px, DecodePixelType = DecodePixelType.Physical };
                await bmp.SetSourceAsync(mem);
                return bmp;
            }
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)px, ThumbnailOptions.ResizeThumbnail);
            if (thumb is null) return null;
            var b = new BitmapImage();
            await b.SetSourceAsync(thumb);
            return b;
        }
        catch (Exception e)
        {
            Log.Write($"снимки: превью {item.Name} не получилось ({e.Message})");
            return null;
        }
    }
}
