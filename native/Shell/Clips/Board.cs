using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsShell.Shots;
using static WindowsShell.Native.Win32;
using D = System.Drawing;

namespace WindowsShell.Clips;

/// Что лежало в буфере обмена в один момент: только то, что умеем вернуть обратно.
public sealed class ClipSnapshot
{
    public string? Text;
    /// «HTML Format» и «Rich Text Format» как есть: вставка с оформлением.
    public byte[]? Html, Rtf;
    /// Картинка: PNG, если программа его дала, иначе DIB (переведём в PNG).
    public byte[]? Png, Dib;
    public List<string>? Files;
    /// Из какой программы скопировали (описание exe), если Windows знает владельца буфера.
    public string? Source;
    public uint Sequence;
}

/// Чтение и запись буфера обмена Windows (design-v2 п. 5).
public static class Board
{
    const uint CF_UNICODETEXT = 13, CF_DIB = 8, CF_HDROP = 15;
    /// Больше не запоминаем: 16 МБ одного формата (огромные таблицы, картинки на всю стену).
    const int MaxBytes = 16 << 20;

    static readonly uint Html = Clip.Format("HTML Format"), Rtf = Clip.Format("Rich Text Format"), Png = Clip.Format("PNG");

    /// Пометки «не запоминать»: так делают менеджеры паролей (KeePass, 1Password, Bitwarden)
    /// и сама Windows для своей истории.
    static readonly uint Exclude = Clip.Format("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint ViewerIgnore = Clip.Format("Clipboard Viewer Ignore");
    static readonly uint CanHistory = Clip.Format("CanIncludeInClipboardHistory");

    /// Метка проверки `--clips-test`: такое оболочка не запоминает, читает только сама проверка.
    public static readonly uint TestMark = Clip.Format("WindowsShell.ClipsTest");

    public static uint Sequence => GetClipboardSequenceNumber();

    /// Прочитать буфер. null — нечего запоминать, `skip` говорит почему.
    public static ClipSnapshot? Read(bool test, out string? skip)
    {
        skip = null;
        var seq = GetClipboardSequenceNumber();
        var opened = false;
        for (var i = 0; i < 10 && !(opened = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(30);
        if (!opened) { skip = "буфер занят другой программой"; return null; }
        var s = new ClipSnapshot { Sequence = seq };
        IntPtr owner;
        try
        {
            if (IsClipboardFormatAvailable(TestMark) != test) { skip = test ? "нет метки проверки" : "проверка оболочки"; return null; }
            if (IsClipboardFormatAvailable(Exclude) || IsClipboardFormatAvailable(ViewerIgnore)) { skip = "помечено «не запоминать» (пароль)"; return null; }
            if (IsClipboardFormatAvailable(CanHistory) && Bytes(CanHistory) is { Length: >= 4 } can && BitConverter.ToInt32(can) == 0)
            { skip = "программа просит не класть в историю (пароль)"; return null; }
            owner = GetClipboardOwner();
            if (IsClipboardFormatAvailable(CF_HDROP)) s.Files = ReadFiles();
            if (s.Files is null && IsClipboardFormatAvailable(CF_UNICODETEXT) && Bytes(CF_UNICODETEXT) is { } text)
            {
                s.Text = Encoding.Unicode.GetString(text).TrimEnd('\0');
                if (s.Text.Length == 0) s.Text = null;
            }
            if (s.Text != null)
            {
                if (IsClipboardFormatAvailable(Html)) s.Html = Bytes(Html);
                if (IsClipboardFormatAvailable(Rtf)) s.Rtf = Bytes(Rtf);
            }
            else if (s.Files is null)
            {
                if (IsClipboardFormatAvailable(Png)) s.Png = Bytes(Png);
                if (s.Png is null && IsClipboardFormatAvailable(CF_DIB)) s.Dib = Bytes(CF_DIB);
            }
        }
        finally
        {
            CloseClipboard();
        }
        if (s.Text is null && s.Files is null && s.Png is null && s.Dib is null) { skip = "нет текста, картинки или файлов"; return null; }
        s.Source = SourceApp(owner);
        return s;
    }

    /// Данные формата (буфер уже открыт). null — нет или слишком большие.
    static byte[]? Bytes(uint format)
    {
        var h = GetClipboardData(format);
        if (h == IntPtr.Zero) return null;
        var size = (long)(ulong)GlobalSize(h);
        if (size <= 0 || size > MaxBytes) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(p, data, 0, (int)size);
            return data;
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    static List<string>? ReadFiles()
    {
        var h = GetClipboardData(CF_HDROP);
        if (h == IntPtr.Zero) return null;
        var n = DragQueryFileW(h, 0xFFFFFFFF, null, 0);
        var list = new List<string>();
        for (uint i = 0; i < n; i++)
        {
            var len = DragQueryFileW(h, i, null, 0);
            var sb = new StringBuilder((int)len + 1);
            DragQueryFileW(h, i, sb, len + 1);
            list.Add(sb.ToString());
        }
        return list.Count > 0 ? list : null;
    }

    /// Название программы-владельца буфера: «Google Chrome», «Telegram Desktop».
    static string? SourceApp(IntPtr owner)
    {
        if (owner == IntPtr.Zero) return null;
        try
        {
            GetWindowThreadProcessId(owner, out var pid);
            if (pid == 0) return null;
            if (pid == (uint)Environment.ProcessId) return "Windows Shell";
            var p = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (p == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                var size = (uint)sb.Capacity;
                if (!QueryFullProcessImageNameW(p, 0, sb, ref size)) return null;
                var path = sb.ToString();
                var name = FileVersionInfo.GetVersionInfo(path).FileDescription;
                return string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name.Trim();
            }
            finally { CloseHandle(p); }
        }
        catch
        {
            return null;
        }
    }

    /// DIB из буфера → PNG (для хранения и миниатюр). null — не разобрать.
    public static byte[]? DibToPng(byte[] dib, out int width, out int height)
    {
        width = height = 0;
        try
        {
            if (dib.Length < 40) return null;
            var headerSize = BitConverter.ToInt32(dib, 0);
            var bpp = BitConverter.ToUInt16(dib, 14);
            var compression = BitConverter.ToInt32(dib, 16);
            var colors = BitConverter.ToInt32(dib, 32);
            if (colors == 0 && bpp <= 8) colors = 1 << bpp;
            // У BITMAPINFOHEADER с BI_BITFIELDS после заголовка три маски цветов.
            var masks = headerSize == 40 && compression == 3 ? 12 : 0;
            var offset = 14 + headerSize + masks + colors * 4;
            using var stream = new MemoryStream(14 + dib.Length);
            var file = new byte[14];
            file[0] = (byte)'B';
            file[1] = (byte)'M';
            BitConverter.GetBytes(14 + dib.Length).CopyTo(file, 2);
            BitConverter.GetBytes(offset).CopyTo(file, 10);
            stream.Write(file);
            stream.Write(dib);
            stream.Position = 0;
            using var bmp = new D.Bitmap(stream);
            width = bmp.Width;
            height = bmp.Height;
            using var png = new MemoryStream();
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            return png.ToArray();
        }
        catch (Exception e)
        {
            Log.Error("буфер: картинка DIB", e);
            return null;
        }
    }

    /// Размер PNG по заголовку (без разбора картинки).
    public static (int W, int H) PngSize(byte[] png) => png.Length >= 24
        ? ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23])
        : (0, 0);

    // ---------- запись ----------

    /// Текст; `html`/`rtf` — оформление (null — простым текстом).
    public static bool PutText(string text, byte[]? html, byte[]? rtf, bool test = false)
    {
        var items = new List<(uint, byte[])> { (CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0")) };
        if (html != null) items.Add((Html, html));
        if (rtf != null) items.Add((Rtf, rtf));
        return Put(items, test);
    }

    public static bool PutImage(byte[] png, bool test = false)
    {
        using var stream = new MemoryStream(png);
        using var bmp = new D.Bitmap(stream);
        return Put([(CF_DIB, Clip.Dib(bmp)), (Png, png)], test);
    }

    public static bool PutFiles(IReadOnlyList<string> paths, bool test = false)
    {
        var text = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var drop = new byte[20 + text.Length];
        BitConverter.GetBytes(20).CopyTo(drop, 0);  // pFiles
        BitConverter.GetBytes(1).CopyTo(drop, 16);  // fWide
        text.CopyTo(drop, 20);
        return Put([(CF_HDROP, drop), (Clip.Format("Preferred DropEffect"), BitConverter.GetBytes(1) /* копировать */)], test);
    }

    /// Положить и, для проверки, свои пометки (`extra` — например, «не запоминать»).
    public static bool Put(List<(uint, byte[])> items, bool test, params (uint, byte[])[] extra)
    {
        if (test) items.Add((TestMark, [1]));
        items.AddRange(extra);
        return Clip.Put(items);
    }

    /// Пометки менеджера паролей (для проверки).
    public static (uint, byte[]) ExcludeMark => (Exclude, [0]);
    public static (uint, byte[]) NoHistoryMark => (CanHistory, BitConverter.GetBytes(0));

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr mem);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFileW(IntPtr drop, uint index, StringBuilder? file, uint size);
}
