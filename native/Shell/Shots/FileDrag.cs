using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace WindowsShell.Shots;

/// Перетащить файл из нашего окна в любую программу (чат, Проводник, браузер), как из
/// Проводника: данные и картинку под курсором даёт сам Проводник (SHDoDragDrop).
///
/// Перетаскивание идёт в своём потоке, а не в потоке интерфейса. В потоке WinUI мышь приходит
/// указателем (WM_POINTER), и цикл перетаскивания OLE не видел зажатую кнопку: бросал файл
/// «в никуда» сразу после начала, обработчик тут же начинал новое — 3–4 раза в секунду, а у
/// окна под курсором оставалась картинка недоделанного перетаскивания. Теперь отпускание
/// кнопки проверяем сами по её настоящему состоянию, а интерфейс оболочки (и док как место,
/// куда можно бросить) во время перетаскивания живёт как обычно.
public static class FileDrag
{
    static int _busy;

    /// Начать тащить файл; сразу возвращается. `done(true)` — файл куда-то бросили; зовётся в
    /// потоке, который начал перетаскивание (без очереди — в потоке перетаскивания).
    /// false — уже тащим другой файл.
    public static bool Start(IntPtr hwnd, string path, Action<bool>? done = null) => Start(hwnd, path, done, null);

    /// `decide` — для проверки (--shots-test): вместо кнопки мыши решает, когда бросить.
    internal static bool Start(IntPtr hwnd, string path, Action<bool>? done, Func<int>? decide)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;
        var queue = DispatcherQueue.GetForCurrentThread();
        var thread = new Thread(() =>
        {
            var dropped = false;
            try { dropped = Run(hwnd, path, decide); }
            finally { Volatile.Write(ref _busy, 0); }
            if (done is null) return;
            if (queue is null || !queue.TryEnqueue(() => done(dropped))) done(dropped);
        }) { IsBackground = true, Name = "перетаскивание" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return true;
    }

    static bool Run(IntPtr hwnd, string path, Func<int>? decide)
    {
        try
        {
            OleInitialize(IntPtr.Zero);
            var iidItem = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidItem, out var item) != 0) return false;
            try
            {
                var bhid = BHID_DataObject;
                var iidData = IID_IDataObject;
                if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iidData, out var data) != 0 || data == IntPtr.Zero) return false;
                try
                {
                    var hr = SHDoDragDrop(hwnd, data, new DropSource(decide), DROPEFFECT_COPY | DROPEFFECT_LINK, out var effect);
                    var dropped = hr == DRAGDROP_S_DROP && effect != 0;
                    Log.Write($"снимки: перетащили {Path.GetFileName(path)} → {(dropped ? $"брошен ({effect})" : hr == DRAGDROP_S_DROP ? "отпустили не над окном, которое его берёт" : $"отмена (0x{hr:X})")}");
                    return dropped;
                }
                finally { Marshal.Release(data); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch (Exception e)
        {
            Log.Error("снимки: перетаскивание", e);
            return false;
        }
        finally { OleUninitialize(); }
    }

    /// Когда бросать: по настоящему состоянию кнопки (GetAsyncKeyState), а не по тому, что
    /// цикл OLE насобирал из сообщений, — в потоке перетаскивания он кнопку не видит.
    [ComVisible(true)]
    sealed class DropSource(Func<int>? decide) : IDropSource
    {
        public int QueryContinueDrag(int escapePressed, int keyState)
        {
            if (decide != null) return decide();
            if (escapePressed != 0 || (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) return DRAGDROP_S_CANCEL;
            // Кнопки поменяны местами в настройках мыши — тащат физической правой.
            var button = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
            return (GetAsyncKeyState(button) & 0x8000) != 0 ? 0 : DRAGDROP_S_DROP;
        }

        public int GiveFeedback(int effect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }

    const uint DROPEFFECT_COPY = 1, DROPEFFECT_LINK = 4;
    internal const int DRAGDROP_S_DROP = 0x00040100, DRAGDROP_S_CANCEL = 0x00040101, DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_ESCAPE = 0x1B, SM_SWAPBUTTON = 23;
    static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bc, ref Guid bhid, ref Guid iid, out IntPtr result);
    }

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")] static extern int SHDoDragDrop(IntPtr hwnd, IntPtr data, [MarshalAs(UnmanagedType.Interface)] IDropSource source, uint effects, out uint effect);
    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern void OleUninitialize();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
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
