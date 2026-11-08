namespace WindowsShell.Island.Core;

public enum GameReason { Marked, MarkedNot, Never, Launcher, FullscreenFrames, None }

/// Правило «это игра» (И8). Старое правило (Perf/Games) знало только папки лаунчеров и список
/// Windows, поэтому игры из D:\Games, Battle.net или VK Play не находились. Новое добавляет
/// общий признак: на переднем плане в настоящем fullscreen и рисует кадры.
public static class GameRule
{
    /// Порядок: ручная отметка → «никогда» (браузеры, плееры, мессенджеры) → лаунчер или
    /// список Windows → весь экран + кадры → не игра.
    public static GameReason Decide(bool? marked, bool inNever, bool launcherKnown, bool fullscreenForeground, bool presentsFrames)
    {
        if (marked is bool m) return m ? GameReason.Marked : GameReason.MarkedNot;
        if (inNever) return GameReason.Never;
        if (launcherKnown) return GameReason.Launcher;
        if (fullscreenForeground && presentsFrames) return GameReason.FullscreenFrames;
        return GameReason.None;
    }

    public static bool IsGame(GameReason r) =>
        r is GameReason.Marked or GameReason.Launcher or GameReason.FullscreenFrames;
}
