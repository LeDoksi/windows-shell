using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WindowsShell.Perf;

/// Что считается игрой (design-v2 п. 8а): программа из списка игр Windows (Game Bar хранит его
/// в GameConfigStore), из папок игр Steam, Epic, GOG, Ubisoft, Xbox, EA, Riot — или отмеченная
/// вручную. Браузеры, плееры и сами лаунчеры не считаются никогда (их можно только отметить
/// вручную). Папки перечитываются в фоне раз в 10 минут.
public static partial class Games
{
    /// Никогда не игра: браузеры, плееры, лаунчеры, мессенджеры, системное.
    static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "msedgewebview2", "firefox", "opera", "opera_gx", "brave", "vivaldi", "browser", "yandex", "iexplore", "arc", "thorium", "librewolf", "waterfox", "tor",
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayermini", "potplayermini64", "potplayer64", "mpv", "wmplayer", "microsoft.media.player", "video.ui", "kmplayer", "aimp", "foobar2000", "spotify", "yandexmusic", "mediaplayer", "obs64", "obs32",
        "steam", "steamwebhelper", "steamservice", "gameoverlayui", "gameoverlayui64", "epicgameslauncher", "epicwebhelper", "galaxyclient", "galaxyclient helper", "ubisoftconnect", "upc", "uplaywebcore", "eadesktop", "eabackgroundservice", "origin", "battle.net", "riotclientservices", "riotclientux", "riotclientuxrender", "xboxpcapp", "gamebar", "playnite.desktopapp", "playnite.fullscreenapp",
        "explorer", "discord", "telegram", "whatsapp", "slack", "teams", "ms-teams", "zoom", "skype", "unitycrashhandler64", "unitycrashhandler32", "crashreportclient", "windowsshellnative",
    };

    static readonly Regex VdfPath = VdfPathRegex();
    static readonly object Lock = new();
    static List<string> _roots = [];
    static HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static long _loadedAt = long.MinValue;
    static bool _loading;

    /// Папки игр, найденные сейчас (для проверки и журнала).
    public static IReadOnlyList<string> Roots { get { lock (Lock) return _roots.ToList(); } }

    /// Сколько игр знает Windows (Game Bar).
    public static int KnownCount { get { lock (Lock) return _known.Count; } }

    /// Игра ли это. `marks` — ручные отметки из настроек.
    public static bool IsGame(string? exe, IReadOnlyDictionary<string, bool> marks) => Why(exe, marks) != null;

    /// Почему это игра (папка Steam, список Windows, вручную), null — не игра.
    public static string? Why(string? exe, IReadOnlyDictionary<string, bool> marks)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        if (marks.TryGetValue(exe, out var marked)) return marked ? "отмечено вручную" : null;
        if (Never.Contains(Path.GetFileNameWithoutExtension(exe))) return null;
        Refresh(wait: false);
        lock (Lock)
        {
            if (Cache.TryGetValue(exe, out var cached)) return cached;
            string? why = null;
            if (_known.Contains(exe)) why = "список игр Windows";
            else if (_roots.FirstOrDefault(r => exe.StartsWith(r, StringComparison.OrdinalIgnoreCase)) is { } root) why = $"папка {root}";
            Cache[exe] = why;
            return why;
        }
    }

    /// Перечитать папки лаунчеров и список Windows (раз в 10 минут, в фоне). `wait` — сразу и дождаться.
    public static void Refresh(bool wait)
    {
        lock (Lock)
        {
            if (_loading || (!wait && Environment.TickCount64 - _loadedAt < 600_000)) return;
            _loading = true;
        }
        if (wait) Load();
        else Task.Run(Load);
    }

    static void Load()
    {
        var roots = new List<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Try("Steam", () => Steam(roots));
        Try("Epic", () => Epic(roots));
        Try("GOG", () => SubkeyValues(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games", "path", roots));
        Try("Ubisoft", () => SubkeyValues(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", "InstallDir", roots));
        Try("папки", () => Folders(roots));
        Try("Windows", () =>
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children is null) return;
            foreach (var name in children.GetSubKeyNames())
            {
                using var k = children.OpenSubKey(name);
                if (k?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) known.Add(exe);
            }
        });
        var clean = roots.Where(r => r.Length > 3)
            .Select(r => r.Replace('/', '\\').TrimEnd('\\') + "\\")
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (Lock)
        {
            _roots = clean;
            _known = known;
            Cache.Clear();
            _loadedAt = Environment.TickCount64;
            _loading = false;
        }
        Log.Write($"игры: папок {clean.Count}, игр в списке Windows {known.Count}");
    }

    static void Try(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Write($"игры: {what}: {e.Message}"); }
    }

    /// Steam: библиотеки из libraryfolders.vdf, игры в steamapps\common.
    static void Steam(List<string> roots)
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (k?.GetValue("SteamPath") is not string steam) return;
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPath.Matches(File.ReadAllText(vdf)))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        foreach (var lib in libs) roots.Add(Path.Combine(lib, "steamapps", "common"));
    }

    /// Epic: манифесты установленных игр.
    static void Epic(List<string> roots)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } path) roots.Add(path);
        }
    }

    static void SubkeyValues(RegistryKey hive, string path, string value, List<string> roots)
    {
        using var k = hive.OpenSubKey(path);
        if (k is null) return;
        foreach (var name in k.GetSubKeyNames())
        {
            using var sub = k.OpenSubKey(name);
            if (sub?.GetValue(value) is string p && p.Length > 0) roots.Add(p);
        }
    }

    /// Стандартные папки лаунчеров на каждом диске: Xbox, EA, Riot, Epic, GOG, Ubisoft.
    static void Folders(List<string> roots)
    {
        string[] names = ["XboxGames", @"Program Files\EA Games", "EA Games", "Riot Games", @"Program Files\Epic Games", "Epic Games",
            @"Program Files (x86)\GOG Galaxy\Games", "GOG Games", @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games"];
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            foreach (var n in names) roots.Add(Path.Combine(drive.RootDirectory.FullName, n));
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPathRegex();
}
