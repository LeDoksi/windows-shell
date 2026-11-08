namespace WindowsShell.Island.Core;

/// Клавиша PrtSc в низкоуровневом хуке (И11): когда это новый снимок, а когда автоповтор.
/// Раньше хватало флага «нажата»: если хук не получил отпускание (Win+L, смена рабочего стола,
/// хук на миг сняли), флаг залипал, и следующее нажатие молча съедалось — «со второго раза».
/// Теперь нажатие — снимок, если клавиша не считалась нажатой ИЛИ с прошлого события PrtSc
/// прошло больше RepeatGap: автоповтор приходит каждые ~30 мс (первый — не позже 1 с).
public sealed class SnapKey
{
    public const uint RepeatGap = 1100;

    bool _down;
    uint _last;
    bool _any;

    /// Что решили по последнему событию (для лога).
    public string Last { get; private set; } = "";

    /// Событие клавиши: down — нажатие, timeMs — время события из хука. true — снимок.
    public bool Step(bool down, uint timeMs)
    {
        var gap = _any ? unchecked(timeMs - _last) : uint.MaxValue;
        _any = true;
        _last = timeMs;
        bool fire;
        if (down)
        {
            fire = !_down || gap > RepeatGap;
            Last = fire ? (_down ? "снимок (залипшее нажатие сброшено)" : "снимок") : "автоповтор";
            _down = true;
        }
        else
        {
            // Пришло только отпускание (нажатие забрал кто-то другой) — снимок по нему.
            fire = !_down;
            Last = fire ? "снимок по отпусканию" : "отпускание";
            _down = false;
        }
        return fire;
    }

    public void Reset()
    {
        _down = false;
        _any = false;
    }
}
