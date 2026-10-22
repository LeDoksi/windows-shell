using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using WindowsShell.Config;

namespace WindowsShell.Perf;

/// Проверка «Монитора ПК» без интерфейса (для проверки через удалённый доступ):
/// `WindowsShellNative.exe --perf-test`. Датчики, видеокарты, что считается игрой, права на FPS,
/// картинки оверлея (perf-overlay-*.png) и настоящий счёт кадров: своё окно DirectX 11 рисует
/// 10 секунд с вертикальной синхронизацией, PresentMon считает его кадры (должно выйти около
/// частоты монитора). Итог — в `perf-test.txt` рядом с exe.
public static class SelfTest
{
    public const string Arg = "--perf-test";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        void Say(string line)
        {
            lock (report) report.AppendLine(line);
            Log.Write($"проверка монитора ПК: {line}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try { Run(Say); }
        catch (Exception e) { Say($"ОШИБКА: {e}"); }
        Say("ГОТОВО");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "perf-test.txt"), report.ToString());
        return true;
    }

    static void Run(Action<string> say)
    {
        say("== Видеокарты (DXGI)");
        foreach (var g in GpuInfo.All())
            say($"   {g.Name}: {g.Vram / 1024 / 1024} МБ, {g.PdhLuid}, температура {g.Temperature()?.ToString("0.0") ?? "нет"}");

        say("== Датчики (3 чтения раз в секунду)");
        var sw = Stopwatch.StartNew();
        using var sensors = new Sensors();
        say($"   запуск {sw.ElapsedMilliseconds} мс, видеокарта «{sensors.GpuName}»");
        PerfSample? last = null;
        for (var i = 0; i < 3; i++)
        {
            Thread.Sleep(1000);
            sw.Restart();
            last = sensors.Read();
            say($"   [{sw.ElapsedMilliseconds} мс] ЦП {last.Cpu:0.0}% {last.CpuTemp?.ToString("0.0") ?? "—"}° ({sensors.CpuTempSource}) · ГП {last.Gpu?.ToString("0.0") ?? "—"}% {last.GpuTemp?.ToString("0.0") ?? "—"}° · " +
                $"ОЗУ {Overlay.Gb(last.RamUsed)}/{Overlay.Gb(last.RamTotal)} ГБ · ВП {(last.VramUsed is { } v ? Overlay.Gb(v) : "—")}/{(last.VramTotal is { } vt ? Overlay.Gb(vt) : "—")} ГБ · " +
                $"сеть ↓{last.NetDown / 1024:0} ↑{last.NetUp / 1024:0} КБ/с");
        }
        var proc = Process.GetCurrentProcess();
        say($"   процессорное время проверки: {proc.TotalProcessorTime.TotalMilliseconds:0} мс");

        say("== Игры");
        Games.Refresh(wait: true);
        say($"   игр в списке Windows: {Games.KnownCount}");
        foreach (var r in Games.Roots) say($"   папка: {r}");
        var marks = new Dictionary<string, bool>();
        foreach (var exe in SampleExes())
            say($"   {exe} → {Games.Why(exe, marks) ?? "не игра"}");

        say("== Права на FPS");
        say($"   {PerfAccess.Current} (группа «{PerfAccess.GroupName}»)");
        say($"   PresentMon: {(File.Exists(Frames.Tool) ? FileVersionInfo.GetVersionInfo(Frames.Tool).FileVersion : "НЕТ ФАЙЛА")}");

        say("== Картинки оверлея");
        var entry = new WidgetEntry { Id = "perf" };
        var s = new PerfSettings(entry) { Shown = PerfSettings.Metrics.ToHashSet() };
        var now = new PerfNow(144, 118, last, "Проверка");
        foreach (var column in new[] { false, true })
        {
            s.Column = column;
            using var bmp = Overlay.Render(s, now, 1.5);
            var file = Path.Combine(AppContext.BaseDirectory, column ? "perf-overlay-column.png" : "perf-overlay-row.png");
            bmp?.Save(file, ImageFormat.Png);
            say($"   {Path.GetFileName(file)}: {bmp?.Width}×{bmp?.Height}");
        }

        say("== Счёт кадров: своё окно DirectX 11, 10 секунд");
        FpsTest(say);
    }

    /// Exe из найденных папок игр и пара заведомо не игр.
    static IEnumerable<string> SampleExes()
    {
        var list = new List<string>();
        foreach (var root in Games.Roots.Take(4))
        {
            try
            {
                var exe = Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).FirstOrDefault();
                if (exe != null) list.Add(exe);
            }
            catch { }
        }
        list.Add(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        list.Add(@"C:\Program Files\VideoLAN\VLC\vlc.exe");
        list.Add(Environment.ProcessPath!);
        return list;
    }

    // ---------- окно DirectX 11 ----------

    static void FpsTest(Action<string> say)
    {
        var dm = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
        if (EnumDisplaySettingsW(null, -1, ref dm)) say($"   частота основного монитора: {dm.dmDisplayFrequency} Гц");

        _proc = DefWindowProcW;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "WindowsShellNative.FpsTest",
        };
        RegisterClassExW(ref wc);
        var hwnd = CreateWindowExW(0x8 /* TOPMOST */, wc.lpszClassName, "Проверка FPS — Windows Shell", 0x10CF0000 /* OVERLAPPEDWINDOW | VISIBLE */,
            120, 120, 640, 360, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { say("   окно не создалось"); return; }
        var desc = new DXGI_SWAP_CHAIN_DESC
        {
            Format = 87, // B8G8R8A8_UNORM
            SampleCount = 1,
            BufferUsage = 0x20, // RENDER_TARGET_OUTPUT
            BufferCount = 2,
            OutputWindow = hwnd,
            Windowed = 1,
            SwapEffect = 4, // FLIP_DISCARD
        };
        var hr = D3D11CreateDeviceAndSwapChain(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, ref desc, out var chain, out var device, out _, out var context);
        if (hr != 0) { say($"   DirectX 11 не создался: 0x{hr:X8}"); DestroyWindow(hwnd); return; }
        var texId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        Method<GetBufferFn>(chain, 9)(chain, 0, ref texId, out var tex);
        Method<CreateRtvFn>(device, 9)(device, tex, IntPtr.Zero, out var rtv);
        Marshal.Release(tex);
        var present = Method<PresentFn>(chain, 8);
        var clear = Method<ClearFn>(context, 50);

        using var frames = new Frames();
        frames.Track(Environment.ProcessId);
        var sw = Stopwatch.StartNew();
        long presents = 0, occluded = 0;
        var nextReport = 3000L;
        var samples = new List<double>();
        while (sw.ElapsedMilliseconds < 10_000)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            var t = sw.ElapsedMilliseconds / 1000f;
            clear(context, rtv, [0.3f + 0.3f * MathF.Sin(t * 3), 0.2f, 0.5f, 1f]);
            var r = present(chain, 1, 0);
            presents++;
            if (r == 0x087A0001) occluded++; // DXGI_STATUS_OCCLUDED
            if (sw.ElapsedMilliseconds >= nextReport)
            {
                nextReport += 1000;
                var fps = frames.Fps;
                if (fps is { } f) samples.Add(f);
                say($"   {sw.ElapsedMilliseconds / 1000.0:0.0} с: FPS {fps?.ToString("0.0") ?? "—"}, вызовов Present {presents}");
            }
        }
        say($"   итог: средний FPS {(samples.Count > 0 ? samples.Average().ToString("0.0") : "—")}, 1% low {frames.Low1?.ToString("0.0") ?? "—"}, Present {presents} за 10 с, закрыто окнами {occluded}");
        if (frames.AccessDenied) say("   НЕТ ПРАВ: PresentMon не открыл сессию событий");
        if (frames.Error.Length > 0) say($"   PresentMon: {frames.Error.Trim().Replace('\n', ' ')}");
        Marshal.Release(rtv);
        Marshal.Release(context);
        Marshal.Release(chain);
        Marshal.Release(device);
        DestroyWindow(hwnd);
        frames.Track(0);
        Thread.Sleep(1500);
    }

    static T Method<T>(IntPtr com, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBufferFn(IntPtr self, uint index, ref Guid iid, out IntPtr surface);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateRtvFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr rtv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PresentFn(IntPtr self, uint sync, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearFn(IntPtr self, IntPtr rtv, float[] color);

    static WndProc? _proc;
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_SWAP_CHAIN_DESC
    {
        public uint Width, Height, RefreshNum, RefreshDen, Format, ScanlineOrdering, Scaling;
        public uint SampleCount, SampleQuality;
        public uint BufferUsage, BufferCount;
        public IntPtr OutputWindow;
        public int Windowed;
        public uint SwapEffect, Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("d3d11.dll")] static extern int D3D11CreateDeviceAndSwapChain(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, ref DXGI_SWAP_CHAIN_DESC desc, out IntPtr chain, out IntPtr device, out int level, out IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODEW dm);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
}
