using System.Diagnostics;
using System.Text;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using WindowsShell.Native;

namespace WindowsShell.Island.Spike;

/// Проба доступа к уведомлениям (И3): `WindowsShellNative.exe --notif-test`. Может ли оболочка
/// без пакета читать уведомления Windows, получать события о новых и убирать их. Чужие
/// уведомления только считаем: ни текста, ни названий приложений в отчёте и в журнале нет —
/// отчёт notif-test.txt пишется только из записи NotifReport, где таких полей нет.
public static class NotifTest
{
    public const string Arg = "--notif-test";
    const string OwnTitle = "Проверка уведомлений", OwnText = "Это проба оболочки, можно закрыть";

    sealed record NotifReport(string Access, bool EventsSupported, string? EventsError, int OthersCount, int OthersWithAumid,
        bool OwnSeen, int OwnLatencyMs, bool OwnTextReadable, bool OwnRemoved, bool WindowsQuiet, bool EventArrived);

    static volatile bool _event;

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        NotifReport? result = null;
        string? error = null;
        // WinRT-вызовы ждём в фоновом потоке (MTA): в STA-потоке Main ожидание может зависнуть.
        var t = new Thread(() =>
        {
            try { result = Run(); }
            catch (Exception e) { error = $"{e.GetType().Name}: {e.Message}"; }
        }) { IsBackground = true, Name = "проба уведомлений" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        var finished = t.Join(30_000);

        var sb = new StringBuilder();
        sb.AppendLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} проба доступа к уведомлениям");
        if (result is { } r)
        {
            sb.AppendLine($"доступ: {r.Access}");
            sb.AppendLine($"подписка на события о новых уведомлениях: {(r.EventsSupported ? "да" : "нет")}{(r.EventsError is null ? "" : $" ({r.EventsError})")}");
            sb.AppendLine($"событие пришло за время пробы (своё или любое другое): {(r.EventArrived ? "да" : "нет")}");
            sb.AppendLine($"чужих уведомлений в центре: {r.OthersCount}, из них с известным приложением: {r.OthersWithAumid}");
            sb.AppendLine($"своё уведомление увидели: {(r.OwnSeen ? $"да, через {r.OwnLatencyMs} мс" : "нет")}");
            sb.AppendLine($"текст своего уведомления прочитан верно: {(r.OwnTextReadable ? "да" : "нет")}");
            sb.AppendLine($"своё уведомление убрали из центра: {(r.OwnRemoved ? "да" : "нет")}");
            sb.AppendLine($"«Не беспокоить» Windows сейчас: {(r.WindowsQuiet ? "включено" : "выключено")}");
        }
        else sb.AppendLine(finished ? $"ОШИБКА: {error}" : "ОШИБКА: проба не уложилась в 30 с");
        sb.AppendLine("ГОТОВО");
        var text = sb.ToString();
        foreach (var line in text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)) Log.Write($"проба уведомлений: {line}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "notif-test.txt"), text);
        return true;
    }

    static NotifReport Run()
    {
        var l = UserNotificationListener.Current;
        var status = l.GetAccessStatus();
        string request;
        try { request = l.RequestAccessAsync().AsTask().Wait(10_000) ? l.GetAccessStatus().ToString() : "запрос не ответил за 10 с"; }
        catch (Exception e) { request = $"запрос: {Short(e)}"; }
        var access = $"до запроса {status}, после запроса {request}";

        var quiet = Tail.Notifications.QuietMode();
        if (l.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            return new NotifReport(access, false, "нет доступа", 0, 0, false, 0, false, false, quiet, false);

        // У программ без пакета подписка может бросать исключение — это и проверяем.
        var eventsSupported = false;
        string? eventsError = null;
        try
        {
            l.NotificationChanged += OnChanged;
            eventsSupported = true;
        }
        catch (Exception e) { eventsError = Short(e); }

        var before = List(l);
        var others = before.Where(n => !IsOwn(n)).ToList();
        var withAumid = others.Count(n => Aumid(n) is { Length: > 0 });

        var known = before.Select(n => n.Id).ToHashSet();
        var sw = Stopwatch.StartNew();
        Toasts.Show(OwnTitle, OwnText, silent: true);
        UserNotification? own = null;
        while (sw.ElapsedMilliseconds < 15_000 && own is null)
        {
            own = List(l).FirstOrDefault(n => !known.Contains(n.Id) && IsOwn(n));
            if (own is null) Thread.Sleep(250);
        }
        var latency = (int)sw.ElapsedMilliseconds;
        var readable = false;
        var removed = false;
        if (own != null)
        {
            try
            {
                var binding = own.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                var texts = binding?.GetTextElements().Select(t => t.Text).ToList() ?? [];
                readable = texts.Count >= 2 && texts[0] == OwnTitle && texts[1] == OwnText;
            }
            catch { }
            try
            {
                l.RemoveNotification(own.Id);
                Thread.Sleep(500);
                removed = List(l).All(n => n.Id != own.Id);
            }
            catch { }
        }
        if (eventsSupported)
            try { l.NotificationChanged -= OnChanged; } catch { }
        return new NotifReport(access, eventsSupported, eventsError, others.Count, withAumid, own != null, own != null ? latency : 0, readable, removed, quiet, _event);
    }

    /// Любое событие после подписки: только факт, без содержимого.
    static void OnChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args) => _event = true;

    static IReadOnlyList<UserNotification> List(UserNotificationListener l) =>
        l.GetNotificationsAsync(NotificationKinds.Toast).AsTask().Result;

    static string? Aumid(UserNotification n)
    {
        try { return n.AppInfo?.AppUserModelId; }
        catch { return null; }
    }

    static bool IsOwn(UserNotification n) => Aumid(n) == Toasts.Aumid;

    /// Тип и код ошибки без текста, который мог бы прийти из чужих данных.
    static string Short(Exception e) => $"{e.GetType().Name} 0x{e.HResult:X8}";
}
