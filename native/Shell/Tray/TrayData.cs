using System.Runtime.InteropServices;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Tray;

/// Одно сообщение `Shell_NotifyIcon`: SHELLTRAYDATA из WM_COPYDATA (dwData = 1). Хэндлы в
/// NOTIFYICONDATAW 32-битные, как у Проводника (проверено спайком WSH-44): hWnd +4, uID +8,
/// uFlags +12, uCallbackMessage +16, hIcon +20, szTip +24 (128 символов), dwState +280,
/// dwStateMask +284, szInfo +288 (256), uVersion +800, szInfoTitle +804 (64), dwInfoFlags +932, guidItem +936.
public sealed record TrayData(uint Message, IntPtr Hwnd, uint Uid, uint Flags, uint Callback, IntPtr Icon, string Tip,
    uint State, uint StateMask, string Info, uint Version, Guid Guid)
{
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_INFO = 0x10, NIF_GUID = 0x20;
    public const uint NIS_HIDDEN = 0x1;
    const uint Signature = 0x34753423;

    public static TrayData? Parse(byte[] b)
    {
        if (b.Length < 8 + 24 || U32(b, 0) != Signature) return null;
        const int n = 8;
        return new TrayData(
            U32(b, 4),
            new IntPtr((nint)U32(b, n + 4)),
            U32(b, n + 8),
            U32(b, n + 12),
            U32(b, n + 16),
            new IntPtr((nint)U32(b, n + 20)),
            Str(b, n + 24, 128),
            U32(b, n + 280),
            U32(b, n + 284),
            Str(b, n + 288, 256),
            U32(b, n + 800),
            b.Length >= n + 952 ? new Guid(b.AsSpan(n + 936, 16)) : Guid.Empty);
    }

    static uint U32(byte[] b, int off) => off + 4 <= b.Length ? BitConverter.ToUInt32(b, off) : 0;

    static string Str(byte[] b, int off, int chars)
    {
        if (off >= b.Length) return "";
        var len = Math.Min(chars * 2, b.Length - off) & ~1;
        var s = System.Text.Encoding.Unicode.GetString(b, off, len);
        var end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }
}

/// HICON → BGRA с умноженной альфой (для WriteableBitmap). Иконка может быть из чужого процесса:
/// USER-объекты общие на сессию, читать можно, удалять — нет.
public static class IconPixels
{
    public static TrayPixels? From(IntPtr hicon)
    {
        if (!GetIconInfo(hicon, out var info)) return null;
        try
        {
            if (info.hbmColor == IntPtr.Zero) return null;
            if (GetObjectW(info.hbmColor, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;
            var color = Bits(info.hbmColor, w, h);
            if (color is null) return null;
            var hasAlpha = false;
            for (var i = 3; i < color.Length; i += 4) if (color[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha)
            {
                // Старая иконка без альфы: прозрачность берём из маски (1 — прозрачно).
                var mask = info.hbmMask != IntPtr.Zero ? Bits(info.hbmMask, w, h) : null;
                for (var i = 0; i < color.Length; i += 4)
                    color[i + 3] = mask != null && mask[i] != 0 ? (byte)0 : (byte)255;
            }
            // У иконок альфа не умножена на цвет, а WriteableBitmap ждёт умноженную.
            for (var i = 0; i < color.Length; i += 4)
            {
                var a = color[i + 3];
                if (a == 255) continue;
                color[i] = (byte)(color[i] * a / 255);
                color[i + 1] = (byte)(color[i + 1] * a / 255);
                color[i + 2] = (byte)(color[i + 2] * a / 255);
            }
            return new TrayPixels(w, h, color);
        }
        catch { return null; }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    static byte[]? Bits(IntPtr bitmap, int w, int h)
    {
        var header = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var data = new byte[w * h * 4];
        var dc = CreateCompatibleDC(IntPtr.Zero);
        try { return GetDIBits(dc, bitmap, 0, (uint)h, data, ref header, 0) == 0 ? null : data; }
        finally { DeleteDC(dc); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
}
