namespace WindowsShell.Island.Core;

/// Что делать со счётом кадров: Track — чей pid считать (0 — ничей); Game — решение
/// (true игра, false не игра, null ещё не решено или не кандидат).
public readonly record struct ProbeDecision(int Track, bool? Game);

/// Проба «игра ли это» для программы, которой нет ни в отметках, ни в лаунчерах (И8): окно
/// впереди на весь экран → считаем его кадры ProbeMs; кадры были — игра, не было — не игра.
/// Решение запоминается до выхода процесса (Forget). Постоянной слежки за всеми процессами нет.
public sealed class GameProbe(int probeMs = 4000)
{
    readonly Dictionary<int, bool> _decided = [];
    int _pid;
    long _since;

    /// Сейчас идёт проба этого процесса.
    public int Probing => _pid;

    public bool? Known(int pid) => _decided.TryGetValue(pid, out var g) ? g : null;

    /// `candidate` — не отмечен вручную, не «никогда», не лаунчер (решать должна проба).
    public ProbeDecision Step(long nowMs, int pid, bool fullscreen, bool candidate, bool frames)
    {
        if (pid == 0 || !candidate) { _pid = 0; return new(0, null); }
        if (_decided.TryGetValue(pid, out var known)) { _pid = 0; return new(known ? pid : 0, known); }
        if (!fullscreen) { _pid = 0; return new(0, null); }
        if (_pid != pid) { _pid = pid; _since = nowMs; }
        if (frames) { _decided[pid] = true; _pid = 0; return new(pid, true); }
        if (nowMs - _since >= probeMs) { _decided[pid] = false; _pid = 0; return new(0, false); }
        return new(pid, null);
    }

    /// Процесс вышел (или отметку поменяли) — решить заново в следующий раз.
    public void Forget(int pid)
    {
        _decided.Remove(pid);
        if (_pid == pid) _pid = 0;
    }
}
