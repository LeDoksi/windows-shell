using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsShell.Island.Core;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Island.Spike;

/// Проба «поверх игры» (И2): `WindowsShellNative.exe --overtop-test [секунд]` (по умолчанию 120).
/// Раз в 250 мс смотрит окно на переднем плане тем же правилом, что Ui/Bars, но помнит
/// fullscreen за каждым монитором (FullscreenMap). На каждом мониторе «в игре» — красная пилюля
/// записи с идущим таймером. Видна ли она в эксклюзивном режиме, скажет Георгий; известно заранее
/// (Charge/ChargeBanner): плашки там не видно. Отчёт — overtop-test.txt рядом с exe.
public static class OvertopTest
{
    public const string Arg = "--overtop-test";

    public static bool Cli(string[] args)
    {
        var i = Array.IndexOf(args, Arg);
        if (i < 0) return false;
        var seconds = i + 1 < args.Length && int.TryParse(args[i + 1], out var s) && s > 0 ? s : 120;
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проба поверх игры: {line}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} проба поверх игры, {seconds} с");
        try { Run(seconds, Say); }
        catch (Exception e) { Say($"ОШИБКА: {e}"); }
        Say("ГОТОВО");
        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "overtop-test.txt"), report.ToString());
        return true;
    }

    static void Run(int seconds, Action<string> say)
    {
        var screens = Monitors.Snapshot();
        say("== Мониторы: " + string.Join("; ", screens.Select(x => $"{x.Name} {x.Bounds.Width}×{x.Bounds.Height} {x.Scale * 100:0} %")));
        var map = new FullscreenMap();
        var pills = new Dictionary<string, Pill>();
        var since = new Dictionary<string, long>();
        var own = (uint)Environment.ProcessId;
        string? lastWhy = null;
        var sw = Stopwatch.StartNew();
        say("== Изменения");
        try
        {
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Pump();
                Thread.Sleep(250);
                screens = Monitors.Snapshot();
                var fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && ProcessId(fg) != own && ClassName(fg) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                    && GetWindowRect(fg, out var r) && screens.FirstOrDefault(x => x.Contains((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)) is { } screen)
                {
                    SHQueryUserNotificationState(out var state);
                    var exclusive = state is 3 or 4;
                    var covers = Covers(r, screen.Bounds);
                    var resizable = ((long)GetWindowLongPtrW(fg, GWL_STYLE) & 0x00040000 /* WS_THICKFRAME */) != 0;
                    var zoomed = IsZoomed(fg);
                    var full = FullscreenRule.IsFull(exclusive, covers, zoomed, resizable);
                    map.Observe(screen.Name, fg, full);
                    var why = $"{screen.Name}: {Exe(fg)}, состояние {state}, весь экран {Yes(covers)}, развёрнуто {Yes(zoomed)}, рамка {Yes(resizable)} → {(full ? "полный экран" : "обычное")}";
                    if (why != lastWhy) say($"   {sw.Elapsed.TotalSeconds,5:0.0} с {why}");
                    lastWhy = why;
                }
                map.Prune((h, name) =>
                {
                    var hw = (IntPtr)h;
                    var sc = screens.FirstOrDefault(x => x.Name == name);
                    return sc != null && IsWindow(hw) && !IsIconic(hw) && GetWindowRect(hw, out var wr) && Covers(wr, sc.Bounds);
                });

                var full2 = map.FullScreens.ToHashSet();
                foreach (var name in pills.Keys.Where(n => !full2.Contains(n)).ToList())
                {
                    pills[name].Dispose();
                    pills.Remove(name);
                    since.Remove(name);
                    say($"   {sw.Elapsed.TotalSeconds,5:0.0} с {name}: пилюля убрана");
                }
                foreach (var name in full2)
                {
                    if (screens.FirstOrDefault(x => x.Name == name) is not { } sc) continue;
                    if (!pills.TryGetValue(name, out var pill))
                    {
                        pill = pills[name] = new Pill();
                        since[name] = Environment.TickCount64;
                    }
                    var t = TimeSpan.FromMilliseconds(Environment.TickCount64 - since[name]);
                    var wasShown = pill.Visible;
                    pill.Show(new RectI(sc.Bounds.Left, sc.Bounds.Top, sc.Bounds.Right, sc.Bounds.Bottom), sc.Scale, $"● запись {(int)t.TotalMinutes}:{t.Seconds:00}");
                    if (!wasShown) say($"   {sw.Elapsed.TotalSeconds,5:0.0} с {name}: пилюля показана");
                }
            }
        }
        finally
        {
            foreach (var p in pills.Values) p.Dispose();
        }
        say("== Видна ли пилюля поверх игры (оконный, «полноэкранный» и эксклюзивный режим) — скажет Георгий");
    }

    static bool Covers(RECT r, RECT s) => r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom;

    static string Yes(bool b) => b ? "да" : "нет";

    static string Exe(IntPtr h)
    {
        try { return Process.GetProcessById((int)ProcessId(h)).ProcessName; }
        catch { return "?"; }
    }

    /// Окна пилюль созданы в этом потоке: разбираем их сообщения, чтобы Windows не сочла их зависшими.
    static void Pump()
    {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
