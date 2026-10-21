using System.Runtime.InteropServices;

namespace WindowsShell.Shots;

/// GDI и прочее Win32 для снимков: захват экрана, окно выбора области, буфер обмена.
internal static class Gdi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public Native.Win32.RECT rcPaint;
        public bool fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const int TRANSPARENT = 1, DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800;
    public const int PS_SOLID = 0, NULL_BRUSH = 5, HALFTONE = 4, COLORONCOLOR = 3;

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, ref Native.Win32.RECT r, bool erase);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref Native.Win32.RECT r, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr dc, string text, int count, ref Native.Win32.RECT r, int format);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(int colorref);
    [DllImport("gdi32.dll")] public static extern IntPtr CreatePen(int style, int width, int colorref);
    [DllImport("gdi32.dll")] public static extern bool Rectangle(IntPtr dc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern bool Ellipse(IntPtr dc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern bool RoundRect(IntPtr dc, int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] public static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] public static extern bool LineTo(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] public static extern int SetTextColor(IntPtr dc, int colorref);
    [DllImport("gdi32.dll")] public static extern bool SetViewportOrgEx(IntPtr dc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern int SelectClipRgn(IntPtr dc, IntPtr rgn);
    [DllImport("gdi32.dll")] public static extern int OffsetClipRgn(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike,
        uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);

    public static int Rgb(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static Native.Win32.RECT Rect(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    public static bool Empty(Native.Win32.RECT r) => r.Right <= r.Left || r.Bottom <= r.Top;

    public static Native.Win32.RECT Intersect(Native.Win32.RECT a, Native.Win32.RECT b) =>
        Rect(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    public static Native.Win32.RECT Inflate(Native.Win32.RECT r, int d) => Rect(r.Left - d, r.Top - d, r.Right + d, r.Bottom + d);
}
