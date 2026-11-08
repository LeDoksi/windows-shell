using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsShell.Shots;

/// Проверка PrtSc у запущенной оболочки (И11): `WindowsShellNative.exe --prtsc-test`.
/// Нажимает PrtSc программно (хук оболочки разбирает такие нажатия как обычные) с разными
/// паузами, в том числе с «потерянным» отпусканием, и ждёт окно выбора области. Окно
/// закрывает Escape-сообщением. Итог — `prtsc-test.txt` рядом с exe и журнал (в журнале
/// оболочки рядом видно её собственные строки «PrtSc ↓ …» и «снимок: …»).
public static class PrtScTest
{
    public const string Arg = "--prtsc-test";
    const string SnipClass = "WindowsShellNative.Snip";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проверка PrtSc: {line}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var ok = 0;
        var total = 0;
        var times = new List<long>();
        try
        {
            var shell = Process.GetProcessesByName("WindowsShellNative").Count(p => p.Id != Environment.ProcessId);
            Say($"процессов оболочки: {shell}");
            // (пауза перед нажатием, держать мс, отпускание дойдёт)
            var plan = new (int Gap, int Hold, bool Up, string What)[]
            {
                (500, 60, true, "обычное нажатие"),
                (300, 60, true, "сразу после прошлого"),
                (1500, 60, true, "через 1,5 с"),
                (200, 60, true, "очень быстро"),
                (800, 700, true, "долгое нажатие"),
                (800, 60, false, "отпускание потеряно"),
                (2000, 60, true, "следующее после потерянного"),
                (500, 60, true, "обычное"),
                (100, 30, true, "сразу"),
                (3000, 60, true, "через 3 с"),
                (400, 60, true, "обычное"),
                (400, 60, true, "обычное"),
            };
            foreach (var (gap, hold, up, what) in plan)
            {
                Thread.Sleep(gap);
                total++;
                var t = Stopwatch.StartNew();
                Key(0x2C, false);
                Thread.Sleep(hold);
                if (up) Key(0x2C, true);
                var h = IntPtr.Zero;
                while (t.ElapsedMilliseconds < 2500 && (h = FindWindowW(SnipClass, null)) == IntPtr.Zero) Thread.Sleep(10);
                if (h != IntPtr.Zero)
                {
                    ok++;
                    times.Add(t.ElapsedMilliseconds);
                    Say($"   OK  {total,2}. {what}: выбор открылся за {t.ElapsedMilliseconds} мс");
                    Thread.Sleep(150);
                    PostMessageW(h, 0x0100 /* WM_KEYDOWN */, 0x1B, IntPtr.Zero);
                    var c = Stopwatch.StartNew();
                    while (c.ElapsedMilliseconds < 2000 && FindWindowW(SnipClass, null) != IntPtr.Zero) Thread.Sleep(10);
                }
                else Say($"   НЕТ {total,2}. {what}: окно выбора не появилось за 2,5 с");
            }
        }
        catch (Exception e) { Say($"ОШИБКА: {e}"); }
        Say($"ГОТОВО: {ok} из {total}{(times.Count > 0 ? $", в среднем {times.Average():0} мс, максимум {times.Max()} мс" : "")}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "prtsc-test.txt"), report.ToString());
        return true;
    }

    static void Key(ushort vk, bool up)
    {
        var input = new INPUT[] { new() { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = 0x37, dwFlags = 1u /* EXTENDED */ | (up ? 2u : 0u) } } };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public int type; [FieldOffset(8)] public KEYBDINPUT ki; }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
