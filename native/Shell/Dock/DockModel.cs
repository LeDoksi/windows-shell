using System.Text.RegularExpressions;
using WindowsShell.Apps;
using WindowsShell.Config;

namespace WindowsShell.Dock;

/// Одна иконка дока (порт `src/dock/model.ts`).
public sealed class DockEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required bool Pinned { get; init; }
    /// Что запускать и откуда брать иконку: AUMID или путь.
    public required string Target { get; init; }
    /// Окна приложения, последнее активное — первым.
    public required IReadOnlyList<WindowInfo> Windows { get; init; }
    public bool Missing { get; init; }
    public bool Running => Windows.Count > 0;
}

public static partial class DockModel
{
    /// Приложения, у которых AUMID в «Пуске» есть, а у окон его нет: окна Проводника
    /// приходят только с explorer.exe.
    static readonly Dictionary<string, string> ExeOfAumid = new(StringComparer.OrdinalIgnoreCase)
    {
        ["microsoft.windows.explorer"] = "\\explorer.exe",
    };

    static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();

    static bool MatchesPin(WindowInfo w, Pin pin, AppTarget target)
    {
        var t = Norm(pin.Target);
        if (w.Aumid != null && Norm(w.Aumid) == t) return true;
        var exe = Norm(w.Exe);
        if (exe == "") return false;
        if (ExeOfAumid.TryGetValue(t, out var tail) && exe.EndsWith(tail)) return true;
        return exe == t || exe == Norm(target.Exe);
    }

    public static string AppKey(WindowInfo w) =>
        Norm(w.Aumid) is { Length: > 0 } a ? a : Norm(w.Exe) is { Length: > 0 } e ? e : $"hwnd:{w.Hwnd}";

    [GeneratedRegex(@"\\windowsapps\\|\\applicationframehost\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex PackagedExe();

    /// Что запускать для незакреплённого приложения. У UWP exe не запускается сам — нужен AUMID.
    public static string LaunchTarget(WindowInfo w)
    {
        if (w.Exe != null && !PackagedExe().IsMatch(w.Exe)) return w.Exe;
        return w.Aumid ?? w.Exe ?? "";
    }

    static IReadOnlyList<WindowInfo> ByRecent(IEnumerable<WindowInfo> ws) =>
        ws.OrderByDescending(w => w.Focused).ThenBy(w => w.Seq).ToList();

    /// Закреплённые в своём порядке, затем незакреплённые запущенные в порядке появления.
    public static List<DockEntry> Build(IReadOnlyList<Pin> pins, IReadOnlyList<WindowInfo> windows)
    {
        var targets = pins.ToDictionary(p => p.Id, p => Launcher.Resolve(p.Target));
        var owner = new Dictionary<IntPtr, string>();
        foreach (var pass in new[] { "aumid", "exe" })
        {
            foreach (var w in windows)
            {
                if (owner.ContainsKey(w.Hwnd)) continue;
                var pin = pins.FirstOrDefault(p => pass == "aumid"
                    ? w.Aumid != null && Norm(w.Aumid) == Norm(p.Target)
                    : MatchesPin(w, p, targets[p.Id]));
                if (pin != null) owner[w.Hwnd] = pin.Id;
            }
        }

        var result = pins.Select(pin =>
        {
            var own = ByRecent(windows.Where(w => owner.GetValueOrDefault(w.Hwnd) == pin.Id));
            return new DockEntry
            {
                Id = pin.Id,
                Title = pin.Title,
                Pinned = true,
                Target = pin.Target,
                Windows = own,
                Missing = own.Count == 0 && !targets[pin.Id].Exists,
            };
        }).ToList();

        var groups = new Dictionary<string, List<WindowInfo>>();
        var order = new List<string>();
        foreach (var w in windows)
        {
            if (owner.ContainsKey(w.Hwnd)) continue;
            var key = AppKey(w);
            if (!groups.TryGetValue(key, out var g)) { groups[key] = g = []; order.Add(key); }
            g.Add(w);
        }
        foreach (var key in order.OrderBy(k => groups[k].Min(w => w.Seq)))
        {
            var own = groups[key];
            var first = own.MinBy(w => w.Seq)!;
            result.Add(new DockEntry
            {
                Id = $"run:{key}",
                Title = string.IsNullOrEmpty(first.AppName) ? first.Title : first.AppName,
                Pinned = false,
                Target = LaunchTarget(first),
                Windows = ByRecent(own),
            });
        }
        return result;
    }
}
