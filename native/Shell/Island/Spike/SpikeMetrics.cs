using System.Diagnostics;
using System.Globalization;
using System.Text;

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
        public double? LatencyMs;
        public int Frames, Long;
        public double MaxFrame;
    }

    sealed record Result(string Screen, string From, string To, double Ms, int Frames, int Long, double MaxFrame, double? LatencyMs, bool ByPointer, bool Interrupted);

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
    public void Begin(string screen, string from, string to, long start, bool byPointer, double periodMs)
    {
        if (_live.Remove(screen, out var old)) Close(screen, old, interrupted: true);
        _live[screen] = new Live { From = from, To = to, Start = start, ByPointer = byPointer, PeriodMs = periodMs };
    }

    /// Кадр анимации на мониторе screen. first — первый кадр после подписки на Rendering
    /// (его длительность — время ожидания, а не кадр, в долгие кадры не идёт).
    public void Frame(string screen, double frameMs, bool first, bool shapeChanged)
    {
        if (!_live.TryGetValue(screen, out var t)) return;
        var now = Now;
        if (t.LatencyMs is null && shapeChanged) t.LatencyMs = Ms(t.Start, now);
        t.Frames++;
        if (first) return;
        _frames.Add(frameMs);
        if (frameMs > 1.5 * t.PeriodMs) t.Long++;
        t.MaxFrame = Math.Max(t.MaxFrame, frameMs);
    }

    public void Settled(string screen)
    {
        if (_live.Remove(screen, out var t)) Close(screen, t, interrupted: false);
    }

    void Close(string screen, Live t, bool interrupted) =>
        _results.Add(new Result(screen, t.From, t.To, Ms(t.Start, Now), t.Frames, t.Long, t.MaxFrame, t.LatencyMs, t.ByPointer, interrupted));

    /// Есть ли хоть один долгий кадр или медленная реакция (для итоговых строк).
    public int LongFrames => _results.Sum(r => r.Long);
    public double MaxLatency => _results.Where(r => r.LatencyMs != null).Select(r => r.LatencyMs!.Value).DefaultIfEmpty(0).Max();

    public string Report(string? cpuLine)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} проба тела острова, стратегия {_strategy}");
        sb.Append(_head);
        sb.AppendLine("== Сквозные клики и кромка");
        foreach (var c in _checks) sb.AppendLine("   " + c);
        sb.AppendLine("== Переходы (из → в): сколько раз, длительность медиана/макс, кадров в среднем, долгих кадров, самый долгий кадр, задержка медиана/макс");
        foreach (var g in _results.GroupBy(r => (r.Screen, r.From, r.To)).OrderBy(g => g.Key.Screen).ThenBy(g => g.Key.From).ThenBy(g => g.Key.To))
        {
            var done = g.Where(r => !r.Interrupted).ToList();
            var lat = g.Where(r => r.LatencyMs != null).Select(r => r.LatencyMs!.Value).ToList();
            var how = g.Any(r => r.ByPointer) ? "нажатие" : "команда";
            sb.AppendLine(string.Format(Ru, "   {0} {1} → {2}: {3} раз (прервано {4}), {5:0}/{6:0} мс, кадров {7:0.0}, долгих {8}, макс. кадр {9:0.0} мс, {10} → кадр {11:0.0}/{12:0.0} мс",
                g.Key.Screen, g.Key.From, g.Key.To, g.Count(), g.Count() - done.Count,
                Median(done.Select(r => r.Ms)), done.Select(r => r.Ms).DefaultIfEmpty(0).Max(),
                g.Average(r => r.Frames), g.Sum(r => r.Long), g.Max(r => r.MaxFrame),
                how, Median(lat), lat.DefaultIfEmpty(0).Max()));
        }
        if (_frames.Count > 0)
            sb.AppendLine(string.Format(Ru, "== Кадры: всего {0}, медиана {1:0.00} мс, 99-й процентиль {2:0.00} мс, макс. {3:0.00} мс",
                _frames.Count, Median(_frames), Percentile(_frames, 0.99), _frames.Max()));
        sb.AppendLine("== ЦП в покое");
        sb.AppendLine("   " + (cpuLine ?? "не мерили (ручной режим)"));
        sb.AppendLine("== Итог");
        sb.AppendLine($"   долгих кадров (длиннее 1,5 периода монитора): {LongFrames}");
        sb.AppendLine(string.Format(Ru, "   самая долгая реакция (нажатие или команда → первый кадр): {0:0.0} мс (цель ≤ 16 мс)", MaxLatency));
        sb.AppendLine("ГОТОВО");
        return sb.ToString();
    }

    static double Median(IEnumerable<double> xs) => Percentile(xs.ToList(), 0.5);

    static double Percentile(List<double> xs, double q)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s[(int)Math.Clamp(Math.Round(q * (s.Count - 1)), 0, s.Count - 1)];
    }
}
