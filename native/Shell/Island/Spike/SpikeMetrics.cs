using System.Diagnostics;
using System.Globalization;
using System.Text;
using WindowsShell.Island.Core;

namespace WindowsShell.Island.Spike;

/// Цифры пробы тела острова: переходы между формами (длительность, кадры, долгие кадры),
/// задержка «нажатие → первый кадр», сквозные клики, ЦП в покое. Отчёт — island-spike-{a|b}.txt.
internal sealed class SpikeMetrics
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    readonly char _strategy;
    readonly StringBuilder _head = new();
    readonly List<Result> _results = [];
    readonly List<double> _frames = [];
    readonly Dictionary<string, Live> _live = [];
    readonly List<string> _checks = [];

    public SpikeMetrics(char strategy) => _strategy = strategy;

    sealed class Live
    {
        public required string From, To;
        public required long Start;
        public required bool ByPointer;
        public required double PeriodMs;
        public required SettleClock Clock;
        public double? LatencyMs;
        public int Frames, Long;
        public double MaxFrame;
    }

    sealed record Result(string Screen, string From, string To, double Ms, double? Ms2, int Frames, int Long, double MaxFrame, double? LatencyMs, double PeriodMs, bool ByPointer, bool Interrupted);

    public static long Now => Stopwatch.GetTimestamp();
    public static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    public void Line(string text)
    {
        _head.AppendLine(text);
        Log.Write($"проба острова: {text}");
    }

    public void Check(string text)
    {
        _checks.Add(text);
        Log.Write($"проба острова: {text}");
    }

    /// Начался переход на мониторе screen. start — момент нажатия (или команды в авторежиме).
    public void Begin(string screen, string from, string to, long start, bool byPointer, double periodMs,
        (double W, double H) fromSize, (double W, double H) toSize)
    {
        if (_live.Remove(screen, out var old)) Close(screen, old, interrupted: true);
        _live[screen] = new Live
        {
            From = from, To = to, Start = start, ByPointer = byPointer, PeriodMs = periodMs,
            Clock = new SettleClock(fromSize.W, fromSize.H, toSize.W, toSize.H),
        };
    }

    /// Такт Rendering (один на все мониторы). first — первый после подписки: его длительность —
    /// время ожидания, а не кадр, в статистику не идёт.
    public void Tick(double frameMs, bool first)
    {
        if (!first) _frames.Add(frameMs);
    }

    /// Кадр анимации на мониторе screen с текущим размером тела.
    public void Frame(string screen, double frameMs, bool first, bool shapeChanged, double w, double h)
    {
        if (!_live.TryGetValue(screen, out var t)) return;
        var now = Now;
        if (t.LatencyMs is null && shapeChanged) t.LatencyMs = Ms(t.Start, now);
        t.Clock.Sample(Ms(t.Start, now), w, h);
        t.Frames++;
        if (first) return;
        if (frameMs > 1.5 * t.PeriodMs) t.Long++;
        t.MaxFrame = Math.Max(t.MaxFrame, frameMs);
    }

    public void Settled(string screen)
    {
        if (_live.Remove(screen, out var t)) Close(screen, t, interrupted: false);
    }

    void Close(string screen, Live t, bool interrupted) =>
        _results.Add(new Result(screen, t.From, t.To, Ms(t.Start, Now), t.Clock.SettledMs, t.Frames, t.Long, t.MaxFrame, t.LatencyMs, t.PeriodMs, t.ByPointer, interrupted));

    /// Есть ли хоть один долгий кадр или медленная реакция (для итоговых строк).
    public int LongFrames => _results.Sum(r => r.Long);
    List<double> Latencies => _results.Where(r => r.LatencyMs != null).Select(r => r.LatencyMs!.Value).ToList();
    List<double> LatencyPeriods => _results.Where(r => r.LatencyMs != null).Select(r => MotionStats.InPeriods(r.LatencyMs!.Value, r.PeriodMs)).ToList();

    public string Report(string? cpuLine)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} проба тела острова, стратегия {_strategy}");
        sb.Append(_head);
        sb.AppendLine("== Сквозные клики и кромка");
        foreach (var c in _checks) sb.AppendLine("   " + c);
        sb.AppendLine("== Переходы (из → в): раз; до 2 % пути медиана/макс (бюджет motion); до полной остановки медиана/макс; кадров; долгих; макс. такт; задержка медиана/макс");
        foreach (var g in _results.GroupBy(r => (r.Screen, r.From, r.To)).OrderBy(g => g.Key.Screen).ThenBy(g => g.Key.From).ThenBy(g => g.Key.To))
        {
            var done = g.Where(r => !r.Interrupted).ToList();
            var arrived = done.Where(r => r.Ms2 != null).Select(r => r.Ms2!.Value).ToList();
            var lat = g.Where(r => r.LatencyMs != null).Select(r => r.LatencyMs!.Value).ToList();
            var how = g.Any(r => r.ByPointer) ? "нажатие" : "команда";
            sb.AppendLine(string.Format(Ru, "   {0} {1} → {2}: {3} раз (прервано {4}); 2 %: {5:0}/{6:0} мс; стоп: {7:0}/{8:0} мс; кадров {9:0.0}, долгих {10}, макс. такт {11:0.0} мс; {12} → кадр {13:0.0}/{14:0.0} мс",
                g.Key.Screen, g.Key.From, g.Key.To, g.Count(), g.Count() - done.Count,
                MotionStats.Percentile(arrived, 0.5), arrived.DefaultIfEmpty(0).Max(),
                Median(done.Select(r => r.Ms)), done.Select(r => r.Ms).DefaultIfEmpty(0).Max(),
                g.Average(r => r.Frames), g.Sum(r => r.Long), g.Max(r => r.MaxFrame),
                how, Median(lat), lat.DefaultIfEmpty(0).Max()));
        }
        if (_frames.Count > 0)
            sb.AppendLine(string.Format(Ru, "== Такты интерфейса (CompositionTarget.Rendering, не показанные кадры): всего {0}, медиана {1:0.00} мс, 99-й процентиль {2:0.00} мс, макс. {3:0.00} мс",
                _frames.Count, Median(_frames), MotionStats.Percentile(_frames, 0.99), _frames.Max()));
        sb.AppendLine("   (один такт на все мониторы; долгие кадры считаются против частоты своего монитора; в авторежиме открываются все мониторы сразу — нагрузка выше обычной)");
        sb.AppendLine("== ЦП в покое");
        sb.AppendLine("   " + (cpuLine ?? "не мерили (ручной режим)"));
        sb.AppendLine("== Итог");
        sb.AppendLine($"   долгих кадров (длиннее 1,5 периода монитора): {LongFrames}");
        var l = Latencies;
        var lp = LatencyPeriods;
        sb.AppendLine(string.Format(Ru, "   задержка (нажатие или команда → первый такт с новой формой): медиана {0:0.0} мс, 95-й процентиль {1:0.0} мс, макс. {2:0.0} мс; в периодах монитора: медиана {3:0.00}, 95-й процентиль {4:0.00}",
            MotionStats.Percentile(l, 0.5), MotionStats.Percentile(l, 0.95), l.DefaultIfEmpty(0).Max(), MotionStats.Percentile(lp, 0.5), MotionStats.Percentile(lp, 0.95)));
        sb.AppendLine("   (такт приходит через 0–1 период после команды, поэтому цель «≤ 16 мс» читаем как «95-й процентиль ≤ 1 периода + 2 мс»)");
        sb.AppendLine("ГОТОВО");
        return sb.ToString();
    }

    static double Median(IEnumerable<double> xs) => MotionStats.Percentile(xs.ToList(), 0.5);
}
