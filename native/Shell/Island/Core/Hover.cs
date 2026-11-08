namespace WindowsShell.Island.Core;

public enum HoverStep { None, Wake, Sleep }

/// Когда лента появляется и прячется (П1): курсор упёрся в верхний край в зоне острова
/// и задержался wakeMs — раскрыть; ушёл с ленты и тела на leaveMs — спрятать. Пока зажата
/// кнопка мыши (тянут окно к Snap) или монитор занят fullscreen, край не будит. Открытый
/// виджет уходом курсора не закрывается — только кликом снаружи или Escape.
public sealed class HoverGate(int wakeMs = 150, int leaveMs = 400)
{
    long _since = -1;
    bool _awake;

    public bool Awake => _awake;

    /// Идёт отсчёт (наведения или ухода): опросу нужны такты и без движения курсора.
    public bool Waiting => _since >= 0;

    /// Остров свернули или раскрыли извне (клик, Escape, событие).
    public void Reset(bool awake = false)
    {
        _awake = awake;
        _since = -1;
    }

    public HoverStep Step(long now, bool atHot, bool insideBody, bool held, bool blocked, bool open)
    {
        if (open)
        {
            _awake = true;
            _since = -1;
            return HoverStep.None;
        }
        if (!_awake)
        {
            if (!atHot || held || blocked) { _since = -1; return HoverStep.None; }
            if (_since < 0) { _since = now; return HoverStep.None; }
            if (now - _since < wakeMs) return HoverStep.None;
            _awake = true;
            _since = -1;
            return HoverStep.Wake;
        }
        if (atHot || insideBody) { _since = -1; return HoverStep.None; }
        if (_since < 0) { _since = now; return HoverStep.None; }
        if (now - _since < leaveMs) return HoverStep.None;
        _awake = false;
        _since = -1;
        return HoverStep.Sleep;
    }
}
