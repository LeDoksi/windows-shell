using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using WindowsShell.Apps;
using WindowsShell.Native;

namespace WindowsShell.Tail;

/// Удалённое: что лежит в корзине и откуда. Данные — файл `$R…` рядом с описанием `$I…`.
public sealed record BinItem(string Name, string OriginalPath, string DataPath, string InfoPath, long Size, DateTime Deleted, bool Folder);

/// Корзина (design-v2 п. 3): сколько в ней, последние удалённые, удалить в неё, восстановить, очистить.
/// Следит за папками `X:\$Recycle.Bin\<SID>` всех локальных дисков; диски не опрашивает
/// (спящий HDD не будим), только раз в 10 минут на случай пропущенного события.
public sealed class RecycleBin : IDisposable
{
    readonly DispatcherQueue _queue;
    readonly DispatcherQueueTimer _debounce, _poll;
    readonly List<FileSystemWatcher> _watchers = [];
    readonly string _sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
    int _version;

    public long Count { get; private set; }
    public long Size { get; private set; }
    public bool Empty => Count == 0;
    public event Action? Changed;

    public RecycleBin(DispatcherQueue queue, MessageWindow messages)
    {
        _queue = queue;
        _debounce = queue.CreateTimer();
        _debounce.IsRepeating = false;
        // Удаление тысячи файлов — тысяча событий: считаем один раз, когда утихнет.
        _debounce.Interval = TimeSpan.FromMilliseconds(400);
        _debounce.Tick += (_, _) => Refresh();
        _poll = queue.CreateTimer();
        _poll.Interval = TimeSpan.FromMinutes(10);
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        messages.Resumed += Refresh;
        Refresh();
    }

    /// Папки корзины текущего пользователя на локальных дисках.
    IEnumerable<string> Folders()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;
            var dir = Path.Combine(d.Name, "$Recycle.Bin", _sid);
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    void Watch()
    {
        try
        {
            foreach (var dir in Folders())
            {
                if (_watchers.Any(x => string.Equals(x.Path, dir, StringComparison.OrdinalIgnoreCase))) continue;
                var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, IncludeSubdirectories = false };
                FileSystemEventHandler on = (_, _) => Poke();
                w.Created += on;
                w.Deleted += on;
                w.Renamed += (_, _) => Poke();
                w.Error += (_, e) => { Log.Write($"корзина: слежение за {dir} сломалось: {e.GetException().Message}"); Poke(); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
        }
        catch (Exception e) { Log.Error("корзина: слежение", e); }
    }

    void Poke() => _queue.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });

    /// Пересчитать в фоне (на большой корзине SHQueryRecycleBin думает заметно).
    public void Refresh()
    {
        // Папка корзины на диске появляется при первом удалении там: подхватываем её.
        Watch();
        var v = ++_version;
        Task.Run(() =>
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            var hr = SHQueryRecycleBinW(null, ref info);
            if (hr != 0) { Log.Write($"корзина: SHQueryRecycleBin 0x{hr:X8}"); return; }
            _queue.TryEnqueue(() =>
            {
                if (v != _version || info.i64NumItems == Count && info.i64Size == Size) return;
                Count = info.i64NumItems;
                Size = info.i64Size;
                Changed?.Invoke();
            });
        });
    }

    /// Последние удалённые, новые первыми. Читает только маленькие файлы `$I…`.
    public List<BinItem> Recent(int count)
    {
        var all = new List<BinItem>();
        foreach (var dir in Folders())
        {
            try
            {
                foreach (var info in Directory.EnumerateFiles(dir, "$I*"))
                    if (ReadInfo(info) is { } item) all.Add(item);
            }
            catch (Exception e) { Log.Write($"корзина: не прочитать {dir}: {e.Message}"); }
        }
        return all.OrderByDescending(i => i.Deleted).Take(count).ToList();
    }

    /// `$I…`: версия 1 (Vista–8.1) — путь фиксированной длины 260 символов; версия 2 (Windows 10+) —
    /// длина пути и путь. Перед путём размер и время удаления (FILETIME).
    static BinItem? ReadInfo(string infoPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoPath);
            if (bytes.Length < 24) return null;
            var version = BitConverter.ToInt64(bytes, 0);
            var size = BitConverter.ToInt64(bytes, 8);
            var deleted = DateTime.FromFileTime(BitConverter.ToInt64(bytes, 16));
            string path;
            if (version == 1)
                path = System.Text.Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24));
            else if (version == 2 && bytes.Length >= 28)
            {
                var chars = BitConverter.ToInt32(bytes, 24);
                path = System.Text.Encoding.Unicode.GetString(bytes, 28, Math.Clamp(chars * 2, 0, bytes.Length - 28));
            }
            else return null;
            var nul = path.IndexOf('\0');
            if (nul >= 0) path = path[..nul];
            var name = Path.GetFileName(infoPath);
            var data = Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + name[2..]);
            var folder = Directory.Exists(data);
            if (!folder && !File.Exists(data)) return null;
            var title = Path.GetFileName(path.TrimEnd('\\'));
            return new BinItem(title.Length > 0 ? title : path, path, data, infoPath, size, deleted, folder);
        }
        catch { return null; }
    }

    /// Положить в корзину, как Delete в Проводнике: без вопроса, а слишком большое Windows
    /// предложит удалить насовсем. Своим потоком: у окошка прогресса должен быть STA.
    public void Delete(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var t = new Thread(() =>
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                pFrom = string.Join('\0', paths) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
            };
            var r = SHFileOperationW(ref op);
            Log.Write($"корзина: удалить {paths.Count} шт. — {(r == 0 ? op.fAnyOperationsAborted ? "отменено" : "готово" : $"ошибка {r}")}");
            Poke();
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// Вернуть на старое место. Там уже есть такое имя — «имя (2)», как делает Проводник.
    public bool Restore(BinItem item)
    {
        try
        {
            var target = Free(item.OriginalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (item.Folder) Directory.Move(item.DataPath, target);
            else File.Move(item.DataPath, target);
            File.Delete(item.InfoPath);
            SHChangeNotify(item.Folder ? SHCNE_MKDIR : SHCNE_CREATE, SHCNF_PATHW, target, null);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, Path.GetDirectoryName(item.InfoPath), null);
            Log.Write($"корзина: восстановлено {target}");
            Poke();
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"корзина: восстановить {item.OriginalPath}", e);
            return false;
        }
    }

    static string Free(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Directory.Exists(path) ? "" : Path.GetExtension(path);
        if (ext.Length == 0) name = Path.GetFileName(path);
        for (var n = 2; ; n++)
        {
            var next = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(next) && !Directory.Exists(next)) return next;
        }
    }

    /// Очистить всё (подтверждение уже спросили мы сами).
    public void EmptyAll()
    {
        Task.Run(() =>
        {
            var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, SHERB_NOCONFIRMATION);
            Log.Write($"корзина: очищена (0x{hr:X8})");
            Poke();
        });
    }

    public static void Open() => Launcher.Open("shell:RecycleBinFolder");

    /// «12 объектов, 340 МБ».
    public string Summary => Count == 0 ? "Корзина пуста" : $"{Objects(Count)}, {SizeText(Size)}";

    public static string Objects(long n)
    {
        var tens = n % 100 / 10;
        var word = tens == 1 ? "объектов" : (n % 10) switch { 1 => "объект", 2 or 3 or 4 => "объекта", _ => "объектов" };
        return $"{n} {word}";
    }

    public static string SizeText(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0} МБ",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} ГБ",
    };

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    const uint FO_DELETE = 3;
    const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;
    const uint SHERB_NOCONFIRMATION = 0x1;
    const int SHCNE_CREATE = 0x2, SHCNE_MKDIR = 0x8, SHCNE_UPDATEDIR = 0x1000;
    const uint SHCNF_PATHW = 0x5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBinW(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int eventId, uint flags, string? item1, string? item2);
}
