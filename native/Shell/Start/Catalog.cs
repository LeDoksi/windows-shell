using System.Runtime.InteropServices;
using Microsoft.Win32;
using WindowsShell.Native;

namespace WindowsShell.Start;

/// Что можно найти и открыть из Пуска.
public enum ResultKind { App, Setting, File, Folder, Command, Web }

/// Одна строка Пуска: программа, страница параметров, файл, команда.
public sealed record StartEntry(ResultKind Kind, string Title, string Target, string Subtitle)
{
    /// Аргументы команды («ping 8.8.8.8»).
    public string? Args { get; init; }
    /// Слова, по которым тоже находится (страницы параметров).
    public string[] Keywords { get; init; } = [];
    /// Значок Segoe Fluent Icons, если иконки от Проводника нет (параметры, интернет).
    public string? Glyph { get; init; }

    /// Имя для иконки от Проводника (путь или `shell:AppsFolder\…`); null — значок Glyph.
    public string? IconSource => Kind switch
    {
        ResultKind.App => Apps.Launcher.ShellName(Target),
        ResultKind.File or ResultKind.Folder => Target,
        ResultKind.Command => Catalog.CommandPath(Target),
        _ => null,
    };
}

/// Списки для Пуска: программы из «Пуска» Windows (shell:AppsFolder), команды «Выполнить»
/// и страницы «Параметров». Программы и команды собираются в фоне и обновляются при открытии.
public sealed class Catalog
{
    readonly object _lock = new();
    IReadOnlyList<StartEntry> _apps = [];
    IReadOnlyList<StartEntry> _commands = [];
    long _appsAt;
    bool _loading;

    /// Список программ поменялся (поток интерфейса).
    public event Action? Changed;

    readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;

    public Catalog(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        _queue = queue;
        Refresh();
    }

    public IReadOnlyList<StartEntry> Apps { get { lock (_lock) return _apps; } }
    public IReadOnlyList<StartEntry> Commands { get { lock (_lock) return _commands; } }
    public static IReadOnlyList<StartEntry> Settings => SettingsPages.All;

    /// Пересобрать в фоне (не чаще раза в 20 секунд).
    public void Refresh()
    {
        lock (_lock)
        {
            if (_loading || Environment.TickCount64 - _appsAt < 20_000 && _apps.Count > 0) return;
            _loading = true;
        }
        var t = new Thread(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var apps = LoadApps();
            var commands = _commands.Count > 0 ? _commands : LoadCommands();
            bool changed;
            lock (_lock)
            {
                changed = !apps.Select(a => a.Target + "|" + a.Title).SequenceEqual(_apps.Select(a => a.Target + "|" + a.Title));
                _apps = apps;
                _commands = commands;
                _appsAt = Environment.TickCount64;
                _loading = false;
            }
            Log.Write($"Пуск: программ {apps.Count}, команд {commands.Count} за {sw.ElapsedMilliseconds} мс");
            if (changed) _queue.TryEnqueue(() => Changed?.Invoke());
        }) { IsBackground = true, Name = "catalog" };
        // Оболочке Windows нужен STA.
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // ---------- программы ----------

    /// Деинсталляторы и справка в «Пуске» не нужны (как `is_noise` в версии на Tauri).
    static bool Noise(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("uninstall") || n.Contains("удалить") || n.Contains("удаление") || n.Contains("деинсталл");
    }

    static List<StartEntry> LoadApps()
    {
        var list = new List<StartEntry>();
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var folder) != 0) return list;
            try
            {
                var bhid = BHID_EnumItems;
                var iidEnum = typeof(IEnumShellItems).GUID;
                if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var ptr) != 0) return list;
                var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(ptr);
                Marshal.Release(ptr);
                try
                {
                    while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
                    {
                        try
                        {
                            var name = Name(item, SIGDN_NORMALDISPLAY);
                            var id = Name(item, SIGDN_PARENTRELATIVEPARSING);
                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || Noise(name)) continue;
                            list.Add(new StartEntry(ResultKind.App, name, id, "Приложение"));
                        }
                        finally { Marshal.ReleaseComObject(item); }
                    }
                }
                finally { Marshal.ReleaseComObject(items); }
            }
            finally { Marshal.ReleaseComObject(folder); }
        }
        catch (Exception e) { Log.Error("список программ", e); }
        return list
            .GroupBy(a => a.Target, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static string? Name(IShellItem item, uint kind)
    {
        if (item.GetDisplayName(kind, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // ---------- команды ----------

    /// Что пишут в «Выполнить» чаще всего: с понятным описанием.
    static readonly (string Name, string Title)[] KnownCommands =
    [
        ("cmd", "Командная строка"),
        ("powershell", "Windows PowerShell"),
        ("wt", "Терминал Windows"),
        ("regedit", "Редактор реестра"),
        ("control", "Панель управления"),
        ("services.msc", "Службы"),
        ("devmgmt.msc", "Диспетчер устройств"),
        ("diskmgmt.msc", "Управление дисками"),
        ("compmgmt.msc", "Управление компьютером"),
        ("eventvwr.msc", "Просмотр событий"),
        ("taskschd.msc", "Планировщик заданий"),
        ("gpedit.msc", "Редактор групповой политики"),
        ("lusrmgr.msc", "Локальные пользователи и группы"),
        ("perfmon", "Системный монитор"),
        ("resmon", "Монитор ресурсов"),
        ("msconfig", "Конфигурация системы"),
        ("msinfo32", "Сведения о системе"),
        ("taskmgr", "Диспетчер задач"),
        ("notepad", "Блокнот"),
        ("calc", "Калькулятор"),
        ("mspaint", "Paint"),
        ("explorer", "Проводник"),
        ("appwiz.cpl", "Программы и компоненты"),
        ("ncpa.cpl", "Сетевые подключения"),
        ("sysdm.cpl", "Свойства системы"),
        ("powercfg.cpl", "Электропитание"),
        ("mmsys.cpl", "Звук (классическое окно)"),
        ("main.cpl", "Свойства мыши"),
        ("inetcpl.cpl", "Свойства браузера"),
        ("firewall.cpl", "Брандмауэр Защитника Windows"),
        ("cleanmgr", "Очистка диска"),
        ("dxdiag", "Средство диагностики DirectX"),
        ("winver", "О программе Windows"),
        ("osk", "Экранная клавиатура"),
        ("magnify", "Экранная лупа"),
        ("snippingtool", "Ножницы"),
        ("mstsc", "Подключение к удалённому рабочему столу"),
        ("charmap", "Таблица символов"),
        ("shell:startup", "Папка автозагрузки"),
        ("%temp%", "Временные файлы"),
        ("%appdata%", "Папка AppData"),
    ];

    static readonly Dictionary<string, string> Titles = KnownCommands.ToDictionary(k => k.Name, k => k.Title, StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase);

    /// Полный путь команды (для иконки и «Открыть расположение»); null — не нашли.
    public static string? CommandPath(string name)
    {
        lock (Paths) return Paths.TryGetValue(name, out var p) ? p : null;
    }

    static List<StartEntry> LoadCommands()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string path) { if (!found.ContainsKey(name)) found[name] = path; }
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Prepend(Environment.SystemDirectory).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".exe" or ".com" or ".bat" or ".cmd") Add(Path.GetFileNameWithoutExtension(f), f);
                    else if (ext is ".msc" or ".cpl") Add(Path.GetFileName(f), f);
                }
            }
            catch { }
        }
        // «Пути приложений» в реестре: так «Выполнить» находит chrome, winword и прочие.
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var k = key.OpenSubKey(sub);
                    if (k?.GetValue(null) is string p && p.Length > 0) Add(Path.GetFileNameWithoutExtension(sub), Environment.ExpandEnvironmentVariables(p.Trim('"')));
                }
            }
            catch { }
        }
        lock (Paths)
        {
            Paths.Clear();
            foreach (var (n, p) in found) Paths[n] = p;
        }
        var list = new List<StartEntry>();
        foreach (var (name, title) in KnownCommands)
            list.Add(new StartEntry(ResultKind.Command, name, name, title) { Keywords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries) });
        foreach (var (name, path) in found)
            if (!Titles.ContainsKey(name)) list.Add(new StartEntry(ResultKind.Command, name, name, path));
        return list;
    }

    /// Описание команды: «Командная строка» или путь.
    public static bool IsKnownCommand(string name) => Titles.ContainsKey(name);

    // ---------- COM ----------

    static Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    const uint SIGDN_NORMALDISPLAY = 0, SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attribs);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems copy);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}
