using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WindowsShell.Notices;

namespace WindowsShell.Tail;

/// Уведомления (WSH-82 → WSH-E26 «центр уведомлений», И3, П18, П25). Оболочка читает центр
/// уведомлений Windows через UserNotificationListener: подписка на события у программы без
/// пакета не работает (итог пробы И3), поэтому раз в секунду сверяем список id и читаем
/// подробности только у новых. Всё новое ложится в собственный центр (`Store`), оригиналы
/// в центре Windows не трогаем (П25, Win+N — запасной путь). Пришедшее, пока оболочки не было,
/// подхватывается при старте тихо. Текст чужих уведомлений в журнал не пишем.
/// Без доступа — по-старому: точку зажигают подсказки программ из трея.
public sealed class Notifications
{
    public static readonly string StorePath = Path.Combine(Config.ShellConfig.Dir, "notices.json");
    static readonly string IconDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsShellNative", "notice-icons");

    readonly DispatcherQueue _queue;
    bool _balloon;
    volatile bool _listener;
    bool _imported;
    HashSet<uint> _seen = [];
    readonly Dictionary<string, string?> _icons = [];
    long _savedTick;
    bool _saveQueued;
    double _pollTotal;
    int _polls;

    public NoticeStore Store { get; } = new(StorePath);

    /// Есть доступ к уведомлениям Windows.
    public bool Listening => _listener;

    /// «Allowed», «Denied», … — для настроек и самопроверки.
    public string Access { get; private set; } = "неизвестно";

    public bool Unread => _listener ? Store.Unread : _balloon;
    public int UnreadCount => _listener ? Store.UnreadCount : _balloon ? 1 : 0;

    /// «Не беспокоить» Windows (баннеры Windows не показываются).
    public bool DoNotDisturb { get; private set; }

    /// Среднее время одного опроса, мс (самопроверка: опрос раз в секунду должен быть дешёвым).
    public double PollMs => _polls == 0 ? 0 : _pollTotal / _polls;

    public event Action? Changed;

    /// Новое уведомление, для которого правило разрешило пилюлю. `Burst` — сколько в пачке
    /// (одного приложения или за один опрос после сна).
    public event Action<Notice, int>? Arrived;

    /// Можно ли пилюлю для этого уведомления (решает остров: своё «Не беспокоить», событие, работает ли).
    public Func<Notice, bool>? PillFor { get; set; }

    public Notifications(DispatcherQueue queue)
    {
        _queue = queue;
        Store.Load();
        if (Store.Expire(DateTimeOffset.Now)) QueueSave();
        _seen = Store.KnownIds();
        new Thread(Loop) { IsBackground = true, Name = "notifications" }.Start();
    }

    void Loop()
    {
        try
        {
            var l = UserNotificationListener.Current;
            var access = l.RequestAccessAsync().AsTask().GetAwaiter().GetResult();
            _listener = access == UserNotificationListenerAccessStatus.Allowed;
            Access = access.ToString();
            Log.Write($"уведомления: доступ {access}");
        }
        catch (Exception e)
        {
            Access = $"ошибка 0x{e.HResult:X8}";
            Log.Write($"уведомления: список недоступен ({e.Message})");
        }
        _queue.TryEnqueue(() => Changed?.Invoke());
        // «Не беспокоить» Windows — раз в секунду (дёшево); список — раз в секунду, если есть доступ.
        for (var tick = 0; ; tick++)
        {
            var dnd = QuietMode();
            if (dnd != DoNotDisturb) _queue.TryEnqueue(() => { DoNotDisturb = dnd; Changed?.Invoke(); });
            if (_listener) Poll();
            if (tick % 3600 == 3599) _queue.TryEnqueue(() => { if (Store.Expire(DateTimeOffset.Now)) { QueueSave(); Changed?.Invoke(); } });
            Thread.Sleep(1000);
        }
    }

    void Poll()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var list = UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
            var fresh = new List<Notice>();
            foreach (var n in list)
            {
                if (_seen.Contains(n.Id)) continue;
                _seen.Add(n.Id);
                if (Read(n) is { } notice) fresh.Add(notice);
            }
            var first = !_imported;
            _imported = true;
            if (fresh.Count > 0) _queue.TryEnqueue(() => Incoming(fresh, quiet: first));
        }
        catch (Exception e)
        {
            Log.Write($"уведомления: опрос не удался (0x{e.HResult:X8})");
        }
        finally
        {
            _pollTotal += sw.Elapsed.TotalMilliseconds;
            _polls++;
        }
    }

    /// Подробности чужого уведомления. Своё (AUMID оболочки) пропускаем: свои события идут островом.
    Notice? Read(UserNotification n)
    {
        try
        {
            string aumid = "", app = "";
            try { aumid = n.AppInfo?.AppUserModelId ?? ""; } catch { }
            if (aumid == Native.Toasts.Aumid) return null;
            try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
            if (app.Length == 0) app = aumid.Length > 0 ? aumid.Split('!')[0] : "Приложение";
            var texts = new List<string>();
            try
            {
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric) ?? n.Notification?.Visual?.Bindings.FirstOrDefault();
                if (binding != null) texts = binding.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList();
            }
            catch { }
            var notice = new Notice
            {
                WinId = n.Id,
                App = app,
                Aumid = aumid,
                Title = texts.FirstOrDefault() ?? app,
                Body = string.Join("\n", texts.Skip(1)),
                Time = n.CreationTime,
                Icon = Icon(n, aumid),
            };
            Log.Write($"уведомление: {app} (длина {notice.Title.Length + notice.Body.Length})");
            return notice;
        }
        catch (Exception e)
        {
            Log.Write($"уведомления: не прочиталось (0x{e.HResult:X8})");
            return null;
        }
    }

    /// Значок приложения: PNG в кэше, один раз на приложение.
    string? Icon(UserNotification n, string aumid)
    {
        var key = aumid.Length > 0 ? aumid : "?";
        if (_icons.TryGetValue(key, out var known)) return known;
        string? path = null;
        try
        {
            var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".png";
            path = Path.Combine(IconDir, name);
            if (!File.Exists(path))
            {
                var logo = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
                if (logo is null) path = null;
                else
                {
                    using var stream = logo.OpenReadAsync().AsTask().GetAwaiter().GetResult();
                    using var input = stream.AsStreamForRead();
                    Directory.CreateDirectory(IconDir);
                    using var file = File.Create(path);
                    input.CopyTo(file);
                }
            }
        }
        catch { path = null; }
        _icons[key] = path;
        return path;
    }

    /// Новые уведомления в центр (поток интерфейса). Первый опрос после старта — тихий подхват.
    void Incoming(List<Notice> fresh, bool quiet)
    {
        if (quiet)
        {
            var n = Store.Import(fresh, DateTimeOffset.Now);
            if (n > 0) Log.Write($"уведомления: подхвачено при старте {n}");
        }
        else
        {
            fresh = fresh.OrderBy(f => f.Time).ToList();
            // После сна или долгой паузы пришло сразу много — одна пилюля, а не залп.
            var many = fresh.Count > 3;
            Notice? pill = null;
            var burst = 0;
            foreach (var n in fresh)
            {
                var allow = !many || n == fresh[^1];
                var r = Store.Add(n, allow && (PillFor?.Invoke(n) ?? false));
                if (r.Pill) { pill = n; burst = many ? fresh.Count : r.Burst; }
            }
            if (pill != null) Arrived?.Invoke(pill, burst);
        }
        QueueSave();
        Changed?.Invoke();
    }

    /// Своё событие оболочки (вместо тоста Windows): в центр и, если можно, пилюлей.
    public void AddOwn(string title, string text)
    {
        var n = new Notice { App = "Windows Shell", Aumid = Native.Toasts.Aumid, Title = title, Body = text, Time = DateTimeOffset.Now };
        var r = Store.Add(n, PillFor?.Invoke(n) ?? false);
        if (r.Pill) Arrived?.Invoke(n, r.Burst);
        QueueSave();
        Changed?.Invoke();
    }

    /// Пилюля закрылась — следующая будет новой пачкой.
    public void EndBurst() => Store.EndBurst();

    /// Программа из трея показала подсказку.
    public void NoteBalloon()
    {
        if (Unread) return;
        _balloon = true;
        Changed?.Invoke();
    }

    /// Открыли центр уведомлений (наш или Windows): всё прочитано.
    public void MarkRead()
    {
        _balloon = false;
        if (Store.Unread) { Store.MarkAllRead(); QueueSave(); }
        Changed?.Invoke();
    }

    public void Remove(Notice n)
    {
        Store.Remove(n);
        QueueSave();
        Changed?.Invoke();
    }

    public void Clear(string app)
    {
        Store.Clear(app);
        QueueSave();
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        Store.ClearAll();
        QueueSave();
        Changed?.Invoke();
    }

    /// Запись на диск в фоне, не чаще раза в 2 с.
    void QueueSave()
    {
        if (_saveQueued) return;
        _saveQueued = true;
        var delay = (int)Math.Max(0, 2000 - (Environment.TickCount64 - _savedTick));
        var t = _queue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delay));
        t.IsRepeating = false;
        t.Tick += (_, _) =>
        {
            _saveQueued = false;
            _savedTick = Environment.TickCount64;
            var json = Store.Serialize();
            Task.Run(() =>
            {
                try { NoticeStore.Write(StorePath, json); }
                catch (Exception e) { Log.Error("уведомления: запись", e); }
            });
        };
        t.Start();
    }

    // «Не беспокоить»: состояние WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED (0 — выключено).
    internal static bool QuietMode()
    {
        try
        {
            var name = 0x0D83063EA3BF1C75UL;
            var buf = new byte[4];
            var size = 4u;
            return NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, buf, ref size) == 0 && size >= 4 && BitConverter.ToInt32(buf) != 0;
        }
        catch { return false; }
    }

    [DllImport("ntdll.dll")] static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr scope, out uint changeStamp, byte[] buffer, ref uint size);
}
