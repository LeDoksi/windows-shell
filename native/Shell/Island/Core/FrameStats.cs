namespace WindowsShell.Island.Core;

/// Где в строке PresentMon нужные колонки (2.x и 1.x называют их по-разному). -1 — колонки нет.
public sealed record FrameColumns(int Time, int Shown, int Presented, int Latency, int Dropped, int Pid, bool QpcTime)
{
    public static FrameColumns Pick(string[] header)
    {
        int Col(params string[] names) => names.Select(n => Array.FindIndex(header, h => h.Trim().Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i >= 0, -1);
        var time = Col("CPUStartQPCTime", "CPUStartTime", "TimeInSeconds");
        return new FrameColumns(
            time,
            // Сколько кадр был на экране: 2.x DisplayedTime, 1.x msBetweenDisplayChange.
            Col("DisplayedTime", "MsBetweenDisplayChange"),
            // Время между кадрами программы: 2.x FrameTime, 1.x msBetweenPresents.
            Col("FrameTime", "MsBetweenPresents"),
            Col("DisplayLatency", "MsUntilDisplayed"),
            Col("Dropped"),
            Col("ProcessID"),
            time >= 0 && header[time].Trim().Equals("CPUStartQPCTime", StringComparison.OrdinalIgnoreCase));
    }
}

/// Кадры игры за последнюю минуту: FPS, 1% low, время кадра, график (И8, П16). Считаем кадры,
/// реально показанные на экране; если PresentMon не знает, когда кадр показан (в некоторых режимах
/// вывода DisplayedTime = NA), — время между кадрами самой программы, пока нет ни одного показанного.
public sealed class FrameStats(double keepMs = 60_000)
{
    // Кольцо кадров: без копий всей минуты на каждый тик (при 500 FPS это 30 тысяч кадров).
    (double At, double Ms)[] _buf = new (double, double)[1024];
    int _start, _count;
    bool _shownSeen;

    (double At, double Ms) this[int i] => _buf[(_start + i) % _buf.Length];

    public int Count => _count;

    /// Кадры известны только как «программа отдала кадр» (показ не виден).
    public bool PresentedOnly => _count > 0 && !_shownSeen;

    public void Clear()
    {
        _start = _count = 0;
        _shownSeen = false;
    }

    /// Показанный кадр: когда показан (мс) и сколько был на экране (мс).
    public void Add(double atMs, double shownMs)
    {
        if (shownMs <= 0) return;
        if (!_shownSeen) { _start = _count = 0; _shownSeen = true; }
        Push(atMs, shownMs);
    }

    /// Кадр без времени показа: время между кадрами программы. Только пока нет показанных.
    public void AddPresented(double atMs, double frameMs)
    {
        if (_shownSeen || frameMs <= 0) return;
        Push(atMs, frameMs);
    }

    void Push(double at, double ms)
    {
        while (_count > 0 && this[0].At < at - keepMs) { _start = (_start + 1) % _buf.Length; _count--; }
        if (_count == _buf.Length)
        {
            var bigger = new (double, double)[_buf.Length * 2];
            for (var i = 0; i < _count; i++) bigger[i] = this[i];
            _buf = bigger;
            _start = 0;
        }
        _buf[(_start + _count) % _buf.Length] = (at, ms);
        _count++;
    }

    /// FPS сейчас: среднее за последние ~0,5 с; null — кадров нет или последний старше 1,5 с.
    public double? Fps(double nowMs)
    {
        if (_count == 0) return null;
        if (nowMs - this[_count - 1].At > 1500) return null;
        double sum = 0;
        var n = 0;
        for (var i = _count - 1; i >= 0 && sum < 500; i--) { sum += this[i].Ms; n++; }
        return sum > 0 ? 1000.0 * n / sum : null;
    }

    /// 1% low за минуту: средний FPS худшего 1 % кадров; меньше 100 кадров — null («—»).
    public double? Low1
    {
        get
        {
            if (_count < 100) return null;
            var times = new double[_count];
            for (var i = 0; i < _count; i++) times[i] = this[i].Ms;
            Array.Sort(times);
            Array.Reverse(times);
            var n = Math.Max(1, times.Length / 100);
            var avg = times.Take(n).Average();
            return avg > 0 ? 1000.0 / avg : null;
        }
    }

    /// Среднее время кадра за последние ~0,5 с, мс.
    public double? FrameMs(double nowMs) => Fps(nowMs) is { } f && f > 0 ? 1000.0 / f : null;

    /// Последние n времён кадра (мс), самое старое первым — для графика.
    public double[] Recent(int n)
    {
        var k = Math.Min(n, _count);
        var r = new double[k];
        for (var i = 0; i < k; i++) r[i] = this[_count - k + i].Ms;
        return r;
    }
}
