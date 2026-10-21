using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static WindowsShell.Native.Win32;
using D = System.Drawing;

namespace WindowsShell.Shots;

/// «Замёрзший» экран: снимок всех мониторов сразу в момент PrtSc (design-v2 п. 4). Из него
/// рисуется окно выбора области (обычный и затемнённый вид) и вырезается готовая картинка.
/// Координаты — физические пиксели экрана, как у окон Windows.
public sealed unsafe class Frozen : IDisposable
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    /// Как было на экране (DIB 32 бита, строки сверху вниз).
    public IntPtr Bright { get; }
    /// То же, затемнённое: всё вне выбранной рамки.
    public IntPtr Dim { get; }

    readonly byte* _bits;

    Frozen(int x, int y, int w, int h, IntPtr bright, byte* bits, IntPtr dim)
    {
        X = x; Y = y; Width = w; Height = h;
        Bright = bright;
        _bits = bits;
        Dim = dim;
    }

    public RECT Bounds => Gdi.Rect(X, Y, X + Width, Y + Height);

    /// Снять весь виртуальный экран (все мониторы).
    public static Frozen Capture()
    {
        var x = Gdi.GetSystemMetrics(Gdi.SM_XVIRTUALSCREEN);
        var y = Gdi.GetSystemMetrics(Gdi.SM_YVIRTUALSCREEN);
        var w = Gdi.GetSystemMetrics(Gdi.SM_CXVIRTUALSCREEN);
        var h = Gdi.GetSystemMetrics(Gdi.SM_CYVIRTUALSCREEN);
        var screen = Gdi.GetDC(IntPtr.Zero);
        var bright = NewDib(screen, w, h, out var bits);
        var dim = NewDib(screen, w, h, out var dimBits);
        var mem = Gdi.CreateCompatibleDC(screen);
        var old = Gdi.SelectObject(mem, bright);
        // CAPTUREBLT: с полупрозрачными окнами (меню, всплывашки), как видит глаз.
        Gdi.BitBlt(mem, 0, 0, w, h, screen, x, y, Gdi.SRCCOPY | Gdi.CAPTUREBLT);
        Gdi.SelectObject(mem, old);
        Gdi.DeleteDC(mem);
        Gdi.ReleaseDC(IntPtr.Zero, screen);

        // Затемнение ~45 %: вне рамки экран темнеет, внутри остаётся как есть.
        var src = (byte*)bits;
        var dst = (byte*)dimBits;
        long n = (long)w * h * 4;
        for (long i = 0; i < n; i += 4)
        {
            dst[i] = (byte)(src[i] * 140 >> 8);
            dst[i + 1] = (byte)(src[i + 1] * 140 >> 8);
            dst[i + 2] = (byte)(src[i + 2] * 140 >> 8);
            dst[i + 3] = 255;
        }
        return new Frozen(x, y, w, h, bright, src, dim);
    }

    static IntPtr NewDib(IntPtr dc, int w, int h, out IntPtr bits)
    {
        var bi = new Gdi.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bmp = Gdi.CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero) throw new InvalidOperationException($"не создать картинку {w}×{h}");
        return bmp;
    }

    /// Вырезать прямоугольник экрана в картинку (непрозрачную).
    public D.Bitmap Crop(RECT r)
    {
        r = Gdi.Intersect(r, Bounds);
        var w = Math.Max(1, r.Width);
        var h = Math.Max(1, r.Height);
        var bmp = new D.Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new D.Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var row = 0; row < h; row++)
            {
                var from = (uint*)(_bits + ((long)(r.Top - Y + row) * Width + (r.Left - X)) * 4);
                var to = (uint*)((byte*)data.Scan0 + (long)row * data.Stride);
                for (var col = 0; col < w; col++) to[col] = from[col] | 0xFF000000;
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    /// Цвет пикселя экрана (для лупы): 0x00RRGGBB.
    public int Pixel(int x, int y)
    {
        if (x < X || y < Y || x >= X + Width || y >= Y + Height) return 0;
        return (int)(((uint*)_bits)[(long)(y - Y) * Width + (x - X)] & 0xFFFFFF);
    }

    public void Dispose()
    {
        Gdi.DeleteObject(Bright);
        Gdi.DeleteObject(Dim);
    }
}

/// Буфер обмена: картинка (DIB и PNG) или файлы (как «Копировать» в Проводнике).
public static class Clip
{
    /// Окно-владелец буфера (окно сообщений оболочки): без владельца Windows не даёт положить данные.
    public static IntPtr Owner { get; set; }

    const uint CF_DIB = 8, CF_HDROP = 15, GMEM_MOVEABLE = 2;

    /// `png` — та же картинка уже в PNG (не кодировать второй раз).
    public static bool SetImage(D.Bitmap bmp, byte[]? png = null)
    {
        if (png is null)
        {
            using var stream = new MemoryStream();
            bmp.Save(stream, ImageFormat.Png);
            png = stream.ToArray();
        }
        return Put([(CF_DIB, Dib(bmp)), (RegisterClipboardFormatW("PNG"), png)]);
    }

    /// Файлы: вставка в Проводник, в Telegram — как файл.
    public static bool SetFiles(IEnumerable<string> paths)
    {
        var list = string.Join("\0", paths) + "\0\0";
        var text = System.Text.Encoding.Unicode.GetBytes(list);
        var drop = new byte[20 + text.Length];
        BitConverter.GetBytes(20).CopyTo(drop, 0);  // pFiles
        BitConverter.GetBytes(1).CopyTo(drop, 16);  // fWide
        text.CopyTo(drop, 20);
        return Put([(CF_HDROP, drop), (RegisterClipboardFormatW("Preferred DropEffect"), BitConverter.GetBytes(1) /* копировать */)]);
    }

    /// DIB 32 бита, строки снизу вверх (так ждут почти все программы).
    public static unsafe byte[] Dib(D.Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var result = new byte[40 + w * h * 4];
        fixed (byte* p = result)
        {
            *(Gdi.BITMAPINFOHEADER*)p = new Gdi.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = h, biPlanes = 1, biBitCount = 32, biSizeImage = w * h * 4 };
            var data = bmp.LockBits(new D.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var row = 0; row < h; row++)
                    Buffer.MemoryCopy((byte*)data.Scan0 + (long)(h - 1 - row) * data.Stride, p + 40 + (long)row * w * 4, (long)w * 4, (long)w * 4);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        return result;
    }

    /// Положить в буфер сразу несколько форматов (всё прежнее из буфера уходит).
    public static bool Put(IReadOnlyList<(uint Format, byte[] Data)> items)
    {
        // Буфер может держать другая программа: пробуем несколько раз.
        var opened = false;
        for (var i = 0; i < 10 && !(opened = OpenClipboard(Owner)); i++) Thread.Sleep(20);
        if (!opened) { Log.Write("буфер обмена занят другой программой"); return false; }
        try
        {
            EmptyClipboard();
            foreach (var (format, data) in items)
            {
                var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
                if (mem == IntPtr.Zero) return false;
                var ptr = GlobalLock(mem);
                Marshal.Copy(data, 0, ptr, data.Length);
                GlobalUnlock(mem);
                if (SetClipboardData(format, mem) == IntPtr.Zero) { GlobalFree(mem); return false; }
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    /// Номер формата по имени («PNG», «HTML Format»).
    public static uint Format(string name) => RegisterClipboardFormatW(name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
}
