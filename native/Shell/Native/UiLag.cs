using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace WindowsShell.Native;

/// Журнал подвисаний интерфейса (WSH-169). Фоновый поток 4 раза в секунду ставит в очередь
/// интерфейса пустое дело и меряет, через сколько оно выполнилось. Дольше 200 мс — строка
/// «интерфейс не отвечал N мс» в журнале: соседние строки подскажут, что тогда делалось.
/// Так медленные места ищутся по дню обычной работы, а не гадаются. Настоящие зависания
/// (десятки секунд) ловит сторож; сон и пробуждение не считаются.
public static class UiLag
{
    const int PeriodMs = 250, ReportMs = 200;
    /// После пробуждения и при запуске интерфейс законно занят: не пишем.
    const long QuietAfterResumeMs = 15_000;
    static long _quietUntil;

    public static void Start(DispatcherQueue queue, MessageWindow messages)
    {
        _quietUntil = Environment.TickCount64 + QuietAfterResumeMs;
        messages.Resumed += () => _quietUntil = Environment.TickCount64 + QuietAfterResumeMs;
        new Thread(() => Loop(queue)) { IsBackground = true, Name = "подвисания", Priority = ThreadPriority.BelowNormal }.Start();
    }

    static void Loop(DispatcherQueue queue)
    {
        using var done = new ManualResetEventSlim();
        var worst = 0L;
        var count = 0;
        var since = Environment.TickCount64;
        while (true)
        {
            Thread.Sleep(PeriodMs);
            try
            {
                done.Reset();
                var sw = Stopwatch.StartNew();
                if (!queue.TryEnqueue(DispatcherQueuePriority.Normal, () => done.Set())) return;
                // Дольше минуты — это уже не подвисание, а зависание или сон: им занимается сторож.
                if (!done.Wait(60_000)) continue;
                var ms = sw.ElapsedMilliseconds;
                var now = Environment.TickCount64;
                if (ms < ReportMs || now < _quietUntil) continue;
                // Сон посреди ожидания: часы ушли дальше, чем ждали.
                if (ms > 30_000) continue;
                Log.Write($"интерфейс не отвечал {ms} мс");
                count++;
                worst = Math.Max(worst, ms);
                // Раз в час — итог, чтобы видеть картину без разбора каждой строки.
                if (now - since > 3_600_000)
                {
                    Log.Write($"подвисаний за час: {count}, самое долгое {worst} мс");
                    (count, worst, since) = (0, 0, now);
                }
            }
            catch
            {
                // Журнал подвисаний не должен ронять оболочку.
            }
        }
    }
}
