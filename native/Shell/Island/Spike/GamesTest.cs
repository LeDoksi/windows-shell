using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using WindowsShell.Island.Core;
using WindowsShell.Native;
using WindowsShell.Perf;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Island.Spike;

/// Проба распознавания игр и FPS (И8): `WindowsShellNative.exe --games-test [секунд]` (90 по
/// умолчанию). Раз в секунду смотрит окно на переднем плане; для каждой новой программы на весь
/// экран 5 с считает её кадры (PresentMon) и сравнивает старое правило (папки лаунчеров и список
/// Windows) с новым (GameRule: весь экран + рисует кадры). Отчёт — games-test.txt рядом с exe.
public static class GamesTest
{
    public const string Arg = "--games-test";

    sealed record Seen(string Exe, bool Full, double? Fps, bool AccessDenied, bool Measured);

    public static bool Cli(string[] args)
    {
        var i = Array.IndexOf(args, Arg);
        if (i < 0) return false;
        var seconds = i + 1 < args.Length && int.TryParse(args[i + 1], out var s) && s > 0 ? s : 90;
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проба игр: {line}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} проба распознавания игр, {seconds} с");
        try { Run(seconds, Say); }
        catch (Exception e) { Say($"ОШИБКА: {e}"); }
        Say("ГОТОВО");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "games-test.txt"), report.ToString());
        return true;
    }

    static void Run(int seconds, Action<string> say)
    {
        say("== Права на FPS");
        bool inGroup;
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
            inGroup = id.Groups?.Any(g => g.Value == "S-1-5-32-559") == true;
        say($"   группа «Пользователи журналов производительности» в текущем входе: {(inGroup ? "да" : "нет (членство действует только после полного выхода из системы)")}");
        say($"   состояние прав оболочки: {PerfAccess.Current}");
        say($"   PresentMon: {(File.Exists(Frames.Tool) ? "есть" : "НЕТ " + Frames.Tool)}");

        say("== Что знает старое правило");
        Games.Refresh(wait: true);
        say($"   папок лаунчеров {Games.Roots.Count}, игр в списке Windows {Games.KnownCount}");
        var marks = ReadMarks();
        say($"   ручных отметок: {marks.Count}");

        say("== Окна на переднем плане");
        var seen = new Dictionary<string, Seen>(StringComparer.OrdinalIgnoreCase);
        var own = (uint)Environment.ProcessId;
        using var frames = new Frames("WindowsShellFpsProbe");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(1000);
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) continue;
            var root = GetAncestor(fg, GA_ROOTOWNER);
            if (root != IntPtr.Zero) fg = root;
            var pid = ProcessId(fg);
            if (pid == own || pid == 0) continue;
            if (ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") continue;
            var exe = ProcessInfo(pid).Exe;
            if (string.IsNullOrEmpty(exe)) continue;
            var full = IsFull(fg);
            if (seen.TryGetValue(exe, out var was) && (was.Measured || !full)) continue;

            if (!full)
            {
                seen[exe] = new Seen(exe, false, null, false, false);
                say($"   {sw.Elapsed.TotalSeconds,4:0} с {Path.GetFileName(exe)}: в окне");
                continue;
            }
            // Новая программа на весь экран: 5 секунд считаем её кадры.
            frames.Track((int)pid);
            double? fps = null;
            for (var t = 0; t < 10; t++)
            {
                Thread.Sleep(500);
                fps = frames.Fps ?? fps;
            }
            var denied = frames.AccessDenied;
            frames.Track(0);
            seen[exe] = new Seen(exe, true, fps, denied, true);
            say($"   {sw.Elapsed.TotalSeconds,4:0} с {Path.GetFileName(exe)}: весь экран, кадры {(fps is { } f ? $"{f:0} FPS" : "нет")}{(denied ? ", PresentMon: нет прав" : "")}");
        }

        say("== Итог: старое правило → новое");
        var none = new Dictionary<string, bool>();
        foreach (var x in seen.Values.OrderBy(v => Path.GetFileName(v.Exe)))
        {
            bool? marked = marks.TryGetValue(x.Exe, out var m) ? m : null;
            var why = Games.Why(x.Exe, marks);
            var reason = GameRule.Decide(marked, Games.InNever(x.Exe), Games.Why(x.Exe, none) != null, x.Full, x.Fps != null);
            say($"   {Path.GetFileName(x.Exe)}: старое: {(why is null ? "не игра" : $"игра ({why})")} → новое: {(GameRule.IsGame(reason) ? "игра" : "не игра")} ({Reason(reason)})");
        }
        if (seen.Count == 0) say("   на переднем плане не было ни одной программы");
    }

    static string Reason(GameReason r) => r switch
    {
        GameReason.Marked => "отмечено вручную",
        GameReason.MarkedNot => "отмечено «не игра»",
        GameReason.Never => "браузер, плеер или мессенджер",
        GameReason.Launcher => "папка лаунчера или список Windows",
        GameReason.FullscreenFrames => "весь экран + кадры",
        _ => "нет признаков",
    };

    /// То же правило fullscreen, что в Ui/Bars.
    static bool IsFull(IntPtr h)
    {
        if (!GetWindowRect(h, out var r)) return false;
        var mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        var s = MonitorInfo(mon).rcMonitor;
        SHQueryUserNotificationState(out var state);
        var covers = r.Left <= s.Left && r.Top <= s.Top && r.Right >= s.Right && r.Bottom >= s.Bottom;
        var resizable = ((long)GetWindowLongPtrW(h, GWL_STYLE) & 0x00040000 /* WS_THICKFRAME */) != 0;
        return FullscreenRule.IsFull(state is 3 or 4, covers, IsZoomed(h), resizable);
    }

    /// Ручные отметки «Монитора ПК» прямо из config.json, только чтение (ShellConfig.Load может писать копии).
    static Dictionary<string, bool> ReadMarks()
    {
        var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = Path.Combine(Config.ShellConfig.Dir, "config.json");
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(f)?["widgets"] is not JsonArray widgets) return d;
            foreach (var w in widgets)
                if (w?["id"]?.GetValue<string>() == "perf" && w["settings"]?["games"] is JsonObject games)
                    foreach (var (k, v) in games)
                        if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) d[k] = b;
        }
        catch (Exception e) { Log.Write($"проба игр: отметки не прочитаны ({e.Message})"); }
        return d;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
