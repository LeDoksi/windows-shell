using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Apps;

/// Иконки от Проводника (IShellItemImageFactory), как `icons.rs`. Достаются в фоне ровно того
/// размера в пикселях, в котором их покажут: Проводник берёт подходящий кадр иконки и уменьшает
/// его качественно, а XAML при уменьшении 256 px в 3–5 раз даёт «лесенку» по краям.
/// Картинка для XAML создаётся в потоке интерфейса. Кэш в памяти на всё время работы.
///
/// Общее правило для «пустых» иконок (WSH-102, Steam стал белым листком): если Проводник отдал
/// заглушку (белый лист или стандартная иконка программы — так бывает, пока программа
/// обновляется и её exe нет), это не иконка: отвечаем null, чтобы взяли запасную (exe, окно),
/// и запоминаем такой ответ только на 20 с — потом спросим снова.
/// `hwnd:<число>` — иконка окна (WM_GETICON / класс окна), последний запасной вариант.
public sealed class Icons(DispatcherQueue queue)
{
    readonly Dictionary<string, WriteableBitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    /// Когда запомнили «нет иконки» (null или заглушка): такой ответ устаревает.
    readonly Dictionary<string, long> _weakAt = new(StringComparer.OrdinalIgnoreCase);
    const long WeakTtlMs = 20_000;

    public const string WindowPrefix = "hwnd:";

    /// Источник «иконка окна» для Get.
    public static string WindowSource(IntPtr hwnd) => $"{WindowPrefix}{(long)hwnd}";
    readonly Dictionary<string, List<Action<WriteableBitmap?>>> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// Иконка по имени для оболочки (путь или `shell:AppsFolder\…`) размером `px` физических
    /// пикселей. Ответ всегда в потоке интерфейса.
    public void Get(string source, int px, Action<WriteableBitmap?> done)
    {
        px = Math.Clamp(px, 16, 256);
        var key = $"{px}|{source}";
        if (_weakAt.TryGetValue(key, out var at) && Environment.TickCount64 - at > WeakTtlMs)
        {
            _weakAt.Remove(key);
            _cache.Remove(key);
        }
        if (_cache.TryGetValue(key, out var ready)) { done(ready); return; }
        if (_waiting.TryGetValue(key, out var list)) { list.Add(done); return; }
        _waiting[key] = [done];
        lock (_jobs) { _jobs.Push((source, px, key)); Monitor.Pulse(_jobs); }
        StartWorkers();
    }

    // Иконки достают несколько фоновых потоков по очереди (свежие запросы первыми): «Все
    // приложения» в Пуске просят сотни иконок сразу, а поток на каждую — слишком много.
    readonly Stack<(string Source, int Px, string Key)> _jobs = new();
    bool _started;

    void StartWorkers()
    {
        if (_started) return;
        _started = true;
        for (var i = 0; i < 4; i++)
        {
            var t = new Thread(Work) { IsBackground = true, Name = "icons" };
            // Оболочке Windows нужен STA.
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    void Work()
    {
        while (true)
        {
            (string Source, int Px, string Key) job;
            lock (_jobs)
            {
                while (_jobs.Count == 0) Monitor.Wait(_jobs);
                job = _jobs.Pop();
            }
            var pixels = job.Source.StartsWith(WindowPrefix) ? RenderWindow(job.Source, job.Px) : Render(job.Source, job.Px);
            if (pixels is { } p && IsGeneric(p, job.Px))
            {
                lock (Reported) if (Reported.Add(job.Source)) Log.Write($"иконки: у {job.Source} заглушка Проводника, берём запасную");
                pixels = null;
            }
            var key = job.Key;
            queue.TryEnqueue(() =>
            {
                WriteableBitmap? bmp = null;
                if (pixels is { } px)
                {
                    bmp = new WriteableBitmap(px.W, px.H);
                    using (var s = bmp.PixelBuffer.AsStream()) s.Write(px.Data, 0, px.Data.Length);
                    bmp.Invalidate();
                }
                _cache[key] = bmp;
                if (bmp is null) _weakAt[key] = Environment.TickCount64;
                if (_waiting.Remove(key, out var callbacks)) foreach (var cb in callbacks) cb(bmp);
            });
        }
    }

    static Guid IID_Factory = typeof(IShellItemImageFactory).GUID;

    // ---------- заглушки Проводника ----------

    // Заглушки того же размера, нарисованные тем же Проводником: пустой файл неизвестного типа
    // (белый лист) и пустой .exe (стандартная иконка программы). Сравниваем побайтно.
    static readonly Dictionary<int, List<byte[]>> Generic = [];
    static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);

    static bool IsGeneric((int W, int H, byte[] Data) image, int px)
    {
        List<byte[]> refs;
        lock (Generic)
        {
            if (!Generic.TryGetValue(px, out refs!))
            {
                refs = [];
                foreach (var name in new[] { "generic.wsh-unknown", "generic.exe" })
                {
                    try
                    {
                        var dir = Path.Combine(Path.GetTempPath(), "WindowsShellNative");
                        Directory.CreateDirectory(dir);
                        var file = Path.Combine(dir, name);
                        if (!File.Exists(file)) File.WriteAllBytes(file, []);
                        if (Render(file, px) is { } g) refs.Add(g.Data);
                    }
                    catch (Exception e) { Log.Write($"иконки: заглушка {name} не получилась ({e.Message})"); }
                }
                Generic[px] = refs;
            }
        }
        return refs.Any(r => r.AsSpan().SequenceEqual(image.Data));
    }

    // ---------- иконка окна ----------

    static (int W, int H, byte[] Data)? RenderWindow(string source, int px)
    {
        if (!long.TryParse(source[WindowPrefix.Length..], out var raw)) return null;
        var hwnd = new IntPtr(raw);
        // Окно программы может не отвечать: ждём недолго.
        IntPtr icon = IntPtr.Zero;
        foreach (var kind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            if (SendMessageTimeoutW(hwnd, WM_GETICON, kind, IntPtr.Zero, SMTO_ABORTIFHUNG, 300, out icon) != IntPtr.Zero && icon != IntPtr.Zero) break;
            icon = IntPtr.Zero;
        }
        if (icon == IntPtr.Zero) icon = GetClassLongPtrW(hwnd, GCLP_HICON);
        if (icon == IntPtr.Zero) icon = GetClassLongPtrW(hwnd, GCLP_HICONSM);
        if (icon == IntPtr.Zero || !GetIconInfo(icon, out var info)) return null;
        try
        {
            if (info.hbmColor == IntPtr.Zero || Pixels(info.hbmColor) is not { } p) return null;
            // У иконок окна альфа не умножена на цвет, а WriteableBitmap ждёт умноженную.
            var d = p.Item3;
            for (var i = 0; i < d.Length; i += 4)
            {
                var a = d[i + 3];
                if (a == 255) continue;
                d[i] = (byte)(d[i] * a / 255);
                d[i + 1] = (byte)(d[i + 1] * a / 255);
                d[i + 2] = (byte)(d[i + 2] * a / 255);
            }
            return p;
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    const uint WM_GETICON = 0x007F, SMTO_ABORTIFHUNG = 0x2;
    static readonly IntPtr ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2;
    const int GCLP_HICON = -14, GCLP_HICONSM = -34;

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern IntPtr GetClassLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    static (int W, int H, byte[] Data)? Render(string source, int px)
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
                    var hr = factory.GetImage(new SIZE { cx = px, cy = px }, SIIGBF_ICONONLY, out bitmap);
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
