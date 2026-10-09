using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Apps;

/// Иконки 256 px от Проводника (IShellItemImageFactory), как `icons.rs`. Достаются в фоне,
/// картинка для XAML создаётся в потоке интерфейса. Кэш в памяти на всё время работы.
public sealed class Icons(DispatcherQueue queue)
{
    public const int Size = 256;

    readonly Dictionary<string, WriteableBitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Action<WriteableBitmap?>>> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// Иконка по имени для оболочки (путь или `shell:AppsFolder\…`). Ответ всегда в потоке интерфейса.
    public void Get(string source, Action<WriteableBitmap?> done)
    {
        if (_cache.TryGetValue(source, out var ready)) { done(ready); return; }
        if (_waiting.TryGetValue(source, out var list)) { list.Add(done); return; }
        _waiting[source] = [done];
        var t = new Thread(() =>
        {
            var pixels = Render(source);
            queue.TryEnqueue(() =>
            {
                WriteableBitmap? bmp = null;
                if (pixels is { } px)
                {
                    bmp = new WriteableBitmap(px.W, px.H);
                    using (var s = bmp.PixelBuffer.AsStream()) s.Write(px.Data, 0, px.Data.Length);
                    bmp.Invalidate();
                }
                _cache[source] = bmp;
                if (_waiting.Remove(source, out var callbacks)) foreach (var cb in callbacks) cb(bmp);
            });
        }) { IsBackground = true };
        // Оболочке Windows нужен STA.
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static Guid IID_Factory = typeof(IShellItemImageFactory).GUID;

    static (int W, int H, byte[] Data)? Render(string source)
    {
        try
        {
            if (SHCreateItemFromParsingName(source, IntPtr.Zero, ref IID_Factory, out var factory) != 0) return null;
            try
            {
                IntPtr bitmap = IntPtr.Zero;
                // Некоторые приложения сначала отвечают E_PENDING: Проводник ещё достаёт картинку.
                for (var tries = 0; tries < 20; tries++)
                {
                    var hr = factory.GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out bitmap);
                    if (hr == E_PENDING) { Thread.Sleep(100); continue; }
                    if (hr != 0) return null;
                    break;
                }
                if (bitmap == IntPtr.Zero) return null;
                try { return Pixels(bitmap); }
                finally { DeleteObject(bitmap); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { return null; }
    }

    /// BGRA сверху вниз. У картинок Проводника альфа уже умножена на цвет — так и нужно
    /// WriteableBitmap. У старых иконок без альфы делаем все пиксели непрозрачными.
    static (int, int, byte[])? Pixels(IntPtr bitmap)
    {
        if (GetObjectW(bitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var header = new BITMAPINFOHEADER
        {
            biSize = 40,
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
        };
        var data = new byte[w * h * 4];
        var dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)h, data, ref header, 0) == 0) return null;
        }
        finally { DeleteDC(dc); }
        var hasAlpha = false;
        for (var i = 3; i < data.Length; i += 4) if (data[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha) for (var i = 3; i < data.Length; i += 4) data[i] = 255;
        return (w, h, data);
    }
}
