namespace WindowsShell.Island.Core;

public enum Arrow { Left, Right, Up, Down }

/// Где пилюля идущей записи (решение Георгия в E22). На экране, который пишется; если он в
/// эксклюзивном fullscreen (поверх него ничего не видно) — на соседнем, с подписью и стрелкой.
/// Пилюля привязана к самой записи: Win на миг сворачивает игру — она не прыгает обратно,
/// а возвращается на свой экран, только когда эксклюзива нет BackMs подряд.
public sealed class RecordRoute(int backMs = 5000)
{
    string? _away;
    long _clearSince = -1;

    public string? Step(long nowMs, string recorded, string? exclusive, ISet<string> full, IReadOnlyList<string> screens)
    {
        if (exclusive == recorded)
        {
            _clearSince = -1;
            var other = screens.FirstOrDefault(s => s != recorded && !full.Contains(s))
                ?? screens.FirstOrDefault(s => s != recorded);
            if (other == null) { _away = null; return recorded; }
            // Уже на соседнем и он ещё есть — остаёмся там.
            if (_away == null || !screens.Contains(_away)) _away = other;
            return _away;
        }
        if (_away != null && screens.Contains(_away))
        {
            if (_clearSince < 0) _clearSince = nowMs;
            if (nowMs - _clearSince < backMs) return _away;
        }
        _away = null;
        _clearSince = -1;
        return recorded;
    }

    /// Пилюля не на своём экране — нужна подпись «пишется экран N» и стрелка.
    public bool Away => _away != null;

    public void Reset()
    {
        _away = null;
        _clearSince = -1;
    }

    /// Куда показывает стрелка с экрана from на экран to (по центрам).
    public static Arrow Direction(RectI from, RectI to)
    {
        var dx = (to.Left + to.Right) / 2.0 - (from.Left + from.Right) / 2.0;
        var dy = (to.Top + to.Bottom) / 2.0 - (from.Top + from.Bottom) / 2.0;
        if (Math.Abs(dx) >= Math.Abs(dy)) return dx < 0 ? Arrow.Left : Arrow.Right;
        return dy < 0 ? Arrow.Up : Arrow.Down;
    }
}
