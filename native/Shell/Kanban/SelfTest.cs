using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace WindowsShell.Kanban;

/// Проверка канбана без интерфейса (для проверки через удалённый доступ):
/// `WindowsShellNative.exe --kanban-test`. Шифрование входа, PKCE, приём ответа браузера,
/// связь с Supabase, включён ли вход через Google и принимает ли Supabase наш адрес возврата.
/// Если вход уже сохранён — читает проекты и задачи (ничего не пишет). Итог — в
/// `kanban-test.txt` рядом с exe.
public static class SelfTest
{
    public const string Arg = "--kanban-test";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        int ok = 0, fail = 0;
        void Say(string line)
        {
            lock (report) report.AppendLine(line);
            Log.Write($"проверка канбана: {line}");
        }
        void Check(string name, bool pass, string detail = "")
        {
            if (pass) ok++; else fail++;
            Say($"{(pass ? "OK  " : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            Run(Check, Say).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Check("без исключений", false, e.ToString());
        }
        Say($"ИТОГ: {ok} OK, {fail} FAIL");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "kanban-test.txt"), report.ToString());
        return true;
    }

    static async Task Run(Action<string, bool, string> check, Action<string> say)
    {
        // RFC 7636, приложение B.
        check("PKCE S256", KanbanApi.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "");
        var (v1, c1) = KanbanApi.Pkce();
        var (v2, _) = KanbanApi.Pkce();
        check("PKCE случайный", v1 != v2 && v1.Length == 43 && c1.Length == 43, "");

        var plain = Encoding.UTF8.GetBytes("{\"refresh\":\"проверка\"}");
        var sealedBytes = KanbanApi.Protect(plain);
        var back = KanbanApi.Unprotect(sealedBytes);
        check("DPAPI шифрует и расшифровывает", back != null && back.SequenceEqual(plain) && !sealedBytes.AsSpan().SequenceEqual(plain), "");

        // Приём ответа браузера: чужой запрос (favicon) не мешает, код читается.
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var wait = KanbanApi.WaitForCode(listener, cts.Token);
            using var local = new HttpClient();
            var fav = await local.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
            var res = await local.GetAsync($"http://127.0.0.1:{port}/?code=abc%2D123&state=x");
            var html = await res.Content.ReadAsStringAsync();
            var code = await wait;
            check("ответ браузера принят", code == "abc-123" && (int)fav.StatusCode == 404 && html.Contains("Готово"), $"код «{code}», favicon {(int)fav.StatusCode}");
        }
        finally { listener.Stop(); }

        try
        {
            using var probe = new TcpListener(System.Net.IPAddress.Loopback, KanbanApi.LoginPort);
            probe.Start();
            probe.Stop();
            check($"порт {KanbanApi.LoginPort} свободен", true, "");
        }
        catch (SocketException e) { check($"порт {KanbanApi.LoginPort} свободен", false, e.Message); }

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("apikey", KanbanApi.Key);

        var settings = await http.GetAsync($"{KanbanApi.Url}/auth/v1/settings");
        var json = JsonNode.Parse(await settings.Content.ReadAsStringAsync());
        var google = json?["external"]?["google"]?.GetValue<bool>() ?? false;
        var email = json?["external"]?["email"]?.GetValue<bool>() ?? false;
        check("Supabase отвечает", settings.IsSuccessStatusCode, $"{(int)settings.StatusCode}");
        check("вход через Google включён", google, "");
        say($"     вход почтой включён: {email}");

        // Принимает ли Supabase адрес возврата: он кладёт его в state (поле referrer); чужой
        // адрес молча заменяется адресом сайта.
        var authorize = await http.GetAsync(KanbanApi.AuthorizeUrl(c1));
        var location = authorize.Headers.Location?.ToString() ?? "";
        var referrer = Referrer(location);
        check("адрес возврата 127.0.0.1 разрешён", referrer?.StartsWith(KanbanApi.RedirectUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true,
            $"Supabase вернёт на «{referrer ?? "?"}» (ответ {(int)authorize.StatusCode})");

        var rest = await http.GetAsync($"{KanbanApi.Url}/rest/v1/projects?select=id&limit=5");
        var anon = JsonNode.Parse(await rest.Content.ReadAsStringAsync()) as JsonArray;
        check("без входа проекты не видны (RLS)", rest.IsSuccessStatusCode && anon is { Count: 0 }, $"{(int)rest.StatusCode}, строк {anon?.Count}");

        var api = new KanbanApi();
        if (!api.SignedIn)
        {
            say("     вход не сохранён: чтение проектов пропущено");
            return;
        }
        say($"     вход сохранён: {api.Email}");
        var projects = await api.Projects();
        var epics = await api.Epics();
        var items = await api.Items();
        check("проекты читаются", projects.Count > 0, $"{projects.Count}: {string.Join(", ", projects.Select(p => p.Prefix))}");
        check("задачи читаются", items.Count > 0, $"{items.Count} (в работе {items.Count(i => i.Status == KStatus.Doing && !i.Archived)}), эпиков {epics.Count}");
        if (projects.FirstOrDefault() is { } first)
        {
            var some = items.FirstOrDefault(i => i.ProjectId == first.Id && !i.Archived);
            if (some != null)
            {
                var details = await api.Details(some.Id);
                var comments = await api.Comments(some.Id);
                check("карточка читается", true, $"{some.Id}: шагов {details.Checklist.Count}, комментариев {comments.Count}");
            }
        }
    }

    /// referrer из state (JWT) в адресе Google.
    static string? Referrer(string location)
    {
        var i = location.IndexOf("state=", StringComparison.Ordinal);
        if (i < 0) return null;
        var state = Uri.UnescapeDataString(location[(i + 6)..].Split('&')[0]);
        var parts = state.Split('.');
        if (parts.Length < 2) return null;
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try
        {
            var payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            return payload?["referrer"]?.GetValue<string>() ?? payload?["site_url"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
