namespace WindowsShell.Island.Spike;

/// Стратегия движения окна: 'a' — окно меняет размер каждый кадр, 'b' — окно неподвижно,
/// меняется только контур и область кликов. Auto — без мыши (для удалённой проверки).
public sealed record SpikeOptions(char Strategy, bool Auto);

/// `--island-spike [a|b] [--auto]`: проба тела острова (И1). Обычный запуск не трогает.
public static class SpikeArgs
{
    public const string Arg = "--island-spike";
    public const string AutoArg = "--auto";

    public static SpikeOptions? Parse(string[] args)
    {
        var i = Array.IndexOf(args, Arg);
        if (i < 0) return null;
        var strategy = 'b';
        if (i + 1 < args.Length && args[i + 1] is "a" or "b") strategy = args[i + 1][0];
        return new SpikeOptions(strategy, args.Contains(AutoArg));
    }
}
