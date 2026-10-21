using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace WindowsShell.Shots;

/// Перетащить файл из нашего окна в любую программу (чат, Проводник, браузер), как из
/// Проводника: данные и картинку под курсором даёт сам Проводник (SHDoDragDrop).
public static class FileDrag
{
    /// Тащить файл; вернётся, когда отпустят кнопку. true — файл куда-то бросили.
    public static bool Start(IntPtr hwnd, string path)
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
                    var hr = SHDoDragDrop(hwnd, data, IntPtr.Zero, DROPEFFECT_COPY | DROPEFFECT_LINK, out var effect);
                    Log.Write($"снимки: перетащили {Path.GetFileName(path)} → {(hr == DRAGDROP_S_DROP ? $"брошен ({effect})" : "отмена")}");
                    return hr == DRAGDROP_S_DROP;
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
    }

    const uint DROPEFFECT_COPY = 1, DROPEFFECT_LINK = 4;
    const int DRAGDROP_S_DROP = 0x00040100;
    static readonly Guid BHID_DataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bc, ref Guid bhid, ref Guid iid, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll")] static extern int SHDoDragDrop(IntPtr hwnd, IntPtr data, IntPtr source, uint effects, out uint effect);
    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
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
