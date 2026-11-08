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
    readonly Queue<(double At, double Ms)> _frames = new();
    bool _shownSeen;

    public int Count => _frames.Count;

    /// Кадры известны только как «программа отдала кадр» (показ не виден).
    public bool PresentedOnly => _frames.Count > 0 && !_shownSeen;

    public void Clear()
    {
        _frames.Clear();
        _shownSeen = false;
    }

    /// Показанный кадр: когда показан (мс) и сколько был на экране (мс).
    public void Add(double atMs, double shownMs)
    {
        if (shownMs <= 0) return;
        if (!_shownSeen) { _frames.Clear(); _shownSeen = true; }
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
        _frames.Enqueue((at, ms));
        while (_frames.Count > 0 && _frames.Peek().At < at - keepMs) _frames.Dequeue();
    }

    /// FPS сейчас: среднее за последние ~0,5 с; null — кадров нет или последний старше 1,5 с.
    public double? Fps(double nowMs)
    {
        if (_frames.Count == 0) return null;
        var arr = _frames.ToArray();
        if (nowMs - arr[^1].At > 1500) return null;
        double sum = 0;
        var n = 0;
        for (var i = arr.Length - 1; i >= 0 && sum < 500; i--) { sum += arr[i].Ms; n++; }
        return sum > 0 ? 1000.0 * n / sum : null;
    }

    /// 1% low за минуту: средний FPS худшего 1 % кадров; меньше 100 кадров — null («—»).
    public double? Low1
    {
        get
        {
            if (_frames.Count < 100) return null;
            var times = _frames.Select(f => f.Ms).OrderByDescending(t => t).ToArray();
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
        var arr = _frames.ToArray();
        return arr.Skip(Math.Max(0, arr.Length - n)).Select(f => f.Ms).ToArray();
    }
}
