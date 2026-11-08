using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Island;

/// Проверка острова снаружи, у запущенной оболочки (для проверки через удалённый доступ):
/// `WindowsShellNative.exe --island-test`. На каждом мониторе: окно острова есть, поверх
/// всех, не попадает в снимки, в покое пропускает клики (кромка — пара пикселей). Затем
/// курсор касается верхнего края — должна открыться лента и ловить мышь; курсор уходит —
/// остров сворачивается. В конце — сколько процессора оболочка тратит в покое (30 с).
/// Курсор возвращается на место. Итог — в `island-test.txt` рядом с exe и в журнале.
public static class SelfTest
{
    public const string Arg = "--island-test";
    public const string WindowTitle = "Остров";
    /// Сообщение окну сообщений оболочки: «проверь волну 2 изнутри».
    public const string SelfCheckMessage = "WindowsShellNative.IslandSelfCheck";
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        int failed = 0;
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проверка острова: {line}");
        }
        void Check(bool ok, string what)
        {
            if (!ok) failed++;
            Say($"   {(ok ? "OK " : "НЕТ")} {what}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            Run(Say, Check);
        }
        catch (Exception e) { Say($"ОШИБКА: {e}"); failed++; }
        Say(failed == 0 ? "ГОТОВО: всё в порядке" : $"ГОТОВО: не прошло {failed}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "island-test.txt"), report.ToString());
        return true;
    }

    static void Run(Action<string> say, Action<bool, string> check)
    {
        // Рядом с оболочкой всегда работает её сторож (тот же exe): оболочка — процесс с окнами острова.
        var all = Process.GetProcessesByName("WindowsShellNative").Where(p => p.Id != Environment.ProcessId).ToList();
        var proc = all.FirstOrDefault(p => Windows((uint)p.Id).Count > 0);
        check(proc != null, $"оболочка с островом запущена (процессов WindowsShellNative: {all.Count})");
        if (proc is null) return;

        var screens = Monitors.Snapshot();
        var islands = Windows((uint)proc.Id);
        say($"== Мониторы: {screens.Count}, окон острова: {islands.Count}");
        check(islands.Count == screens.Count, "по окну острова на каждом мониторе");

        GetCursorPos(out var saved);
        try
        {
            foreach (var screen in screens)
            {
                say($"== {screen.Name}{(screen.Primary ? " (основной)" : "")}, {screen.Bounds.Right - screen.Bounds.Left}×{screen.Bounds.Bottom - screen.Bounds.Top}, масштаб {screen.Scale:0.##}");
                var cx = (screen.Bounds.Left + screen.Bounds.Right) / 2;
                var h = islands.FirstOrDefault(w => GetWindowRect(w, out var r) && cx >= r.Left && cx < r.Right && r.Top == screen.Bounds.Top);
                check(h != IntPtr.Zero, "окно острова у верхнего края по центру");
                if (h == IntPtr.Zero) continue;
                var ex = ExStyle(h);
                check((ex & WS_EX_TOPMOST) != 0, "поверх всех окон");
                check((ex & WS_EX_LAYERED) != 0 && (ex & WS_EX_NOACTIVATE) != 0, "не забирает фокус, пока не кликнули");
                check(GetWindowDisplayAffinity(h, out var aff) && aff == 0x11, $"не попадает в снимки и запись (affinity {aff:X})");

                // Покой: курсор посередине экрана.
                SetCursorPos(cx, (screen.Bounds.Top + screen.Bounds.Bottom) / 2);
                Thread.Sleep(900);
                var rest = Region(h);
                say($"   покой: область {rest.W}×{rest.H} px, сквозная {(ExStyle(h) & WS_EX_TRANSPARENT) != 0}");
                if (rest.H > 20)
                {
                    say("   (остров раскрыт или на мониторе игра/видео на весь экран — наведение не проверяем)");
                    continue;
                }
                check((ExStyle(h) & WS_EX_TRANSPARENT) != 0, "в покое клики проходят сквозь остров");
                check(rest.H <= Math.Ceiling(6 * screen.Scale), "в покое — только тонкая кромка");

                // Касание края: лента.
                var sw = Stopwatch.StartNew();
                SetCursorPos(cx, screen.Bounds.Top);
                var open = default((int W, int H));
                while (sw.ElapsedMilliseconds < 2000)
                {
                    Thread.Sleep(50);
                    open = Region(h);
                    if (open.H >= 40 * screen.Scale && sw.ElapsedMilliseconds > 400) break;
                }
                say($"   касание края: область {open.W}×{open.H} px через {sw.ElapsedMilliseconds} мс");
                check(open.H >= 40 * screen.Scale, "курсор у края — открылась лента");
                check((ExStyle(h) & WS_EX_TRANSPARENT) == 0, "лента ловит мышь");

                // Ушли: свернулся.
                SetCursorPos(cx, (screen.Bounds.Top + screen.Bounds.Bottom) / 2);
                sw.Restart();
                var back = open;
                while (sw.ElapsedMilliseconds < 3000)
                {
                    Thread.Sleep(50);
                    back = Region(h);
                    if (back.H <= 10 * screen.Scale && (ExStyle(h) & WS_EX_TRANSPARENT) != 0) break;
                }
                say($"   курсор ушёл: область {back.W}×{back.H} px через {sw.ElapsedMilliseconds} мс");
                check(back.H <= Math.Ceiling(6 * screen.Scale) && (ExStyle(h) & WS_EX_TRANSPARENT) != 0, "курсор ушёл — остров свернулся в кромку");
            }
        }
        finally { SetCursorPos(saved.X, saved.Y); }

        say("== Изнутри: снимки, корзина, полка, монитор ПК, канбан, лист настроек, пилюля записи, закрепление");
        try
        {
            var msgWin = IntPtr.Zero;
            EnumWindows((hw, _) =>
            {
                if (ProcessId(hw) == (uint)proc.Id && ClassName(hw) == "WindowsShellNative.Messages") { msgWin = hw; return false; }
                return true;
            }, IntPtr.Zero);
            check(msgWin != IntPtr.Zero, "окно сообщений оболочки найдено");
            if (msgWin != IntPtr.Zero)
            {
                var mark = DateTime.Now;
                PostMessageW(msgWin, RegisterWindowMessageW(SelfCheckMessage), IntPtr.Zero, IntPtr.Zero);
                var sw2 = Stopwatch.StartNew();
                List<string> got = [];
                while (sw2.ElapsedMilliseconds < 90_000)
                {
                    Thread.Sleep(500);
                    got = ReadTail(Log.File, 300).Where(l => l.Contains("остров: самопроверка:") && Stamp(l) >= mark.AddSeconds(-1)).ToList();
                    if (got.Any(l => l.Contains("ГОТОВО"))) break;
                }
                foreach (var l in got) say($"   {l[(l.IndexOf("самопроверка:") + 14)..]}");
                check(got.Any(l => l.Contains("ГОТОВО")), "самопроверка изнутри закончилась");
                var misses = got.Count(l => l.Contains("самопроверка: НЕТ"));
                check(misses == 0, $"изнутри всё прошло (не прошло {misses})");
            }
        }
        catch (Exception e) { say($"   изнутри не проверено: {e.Message}"); }

        say("== Покой 30 с (курсор не трогаем)");
        Thread.Sleep(1500);
        proc.Refresh();
        var cpu0 = proc.TotalProcessorTime;
        var t0 = Stopwatch.StartNew();
        Thread.Sleep(30_000);
        proc.Refresh();
        var used = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        var percent = used / t0.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        say($"   процессор оболочки: {percent:0.00} % (всего {used:0} мс за 30 с), память {proc.WorkingSet64 / 1024 / 1024} МБ");
        check(percent < 1, "в покое оболочка почти не тратит процессор");

        say("== Журнал оболочки (остров)");
        try
        {
            // Только строки самой оболочки («остров: …»), без проб и этой проверки.
            var lines = ReadTail(Log.File, 400).Where(l => l.Contains(" остров: ") || l.Contains("ОШИБКА в остров")).TakeLast(15).ToList();
            foreach (var l in lines) say($"   {l}");
            var errors = lines.Count(l => l.Contains("ОШИБКА"));
            check(errors == 0, $"ошибок острова в журнале нет ({errors})");
        }
        catch (Exception e) { say($"   журнал не прочитан: {e.Message}"); }
    }

    static List<IntPtr> Windows(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (ProcessId(h) == pid && IsWindowVisible(h) && WindowText(h) == WindowTitle) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// Размер области окна (SetWindowRgn); нет области — всё окно.
    static (int W, int H) Region(IntPtr h)
    {
        var kind = GetWindowRgnBox(h, out var box);
        if (kind is 2 or 3) return (box.Right - box.Left, box.Bottom - box.Top);
        if (kind == 1) return (0, 0);
        GetWindowRect(h, out var r);
        return (r.Right - r.Left, r.Bottom - r.Top);
    }

    static IEnumerable<string> ReadTail(string file, int count)
    {
        using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(f, Encoding.UTF8);
        var q = new Queue<string>();
        while (reader.ReadLine() is { } line)
        {
            q.Enqueue(line);
            if (q.Count > count) q.Dequeue();
        }
        return q;
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);

    /// Время строки журнала (начало строки); не разобрали — самое раннее.
    static DateTime Stamp(string line)
    {
        var head = line.Length >= 12 ? line[..12] : line;
        return TimeSpan.TryParseExact(head, @"hh\:mm\:ss\.fff", null, out var t) ? DateTime.Today + t : DateTime.MinValue;
    }
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr h, out uint affinity);
    [DllImport("user32.dll")] static extern int GetWindowRgnBox(IntPtr h, out RECT box);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
}
