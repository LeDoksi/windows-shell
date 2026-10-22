using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsShell.Kanban;

/// Вход в канбан: токены Supabase и почта, под которой вошли.
public sealed record KSession(string Access, string Refresh, DateTimeOffset Expires, string Email);

/// Канбан Георгия напрямую: Supabase (REST и вход), та же база, что у сайта. Все таблицы
/// открыты только владельцу (RLS по почте в токене), поэтому без входа ничего не видно.
/// Вход: Google через браузер (PKCE, ответ ловим на 127.0.0.1) или почта и пароль. Токены лежат
/// зашифрованными (DPAPI, только этот пользователь Windows) и сами обновляются.
public sealed class KanbanApi
{
    public const string Url = "https://qcxfaxgqjzabtrzpsnnm.supabase.co";
    /// Публичный ключ проекта (тот же, что у сайта): сам по себе ничего не открывает.
    public const string Key = "sb_publishable_x1pPLK_8RsTYsiok6u01Yg_ZcqOw7QI";
    public const string Site = "https://ledoksi.github.io/kanban/";
    /// Порт, на который Supabase возвращает вход через Google. Адрес http://127.0.0.1:47613/**
    /// должен быть в Supabase → Authentication → URL Configuration → Redirect URLs.
    public const int LoginPort = 47613;
    public static string RedirectUrl => $"http://127.0.0.1:{LoginPort}/";

    static readonly string SessionFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsShellNative", "kanban-session.bin");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    KSession? _session;

    public KanbanApi() => _session = LoadSession();

    public bool SignedIn => _session != null;
    public string? Email => _session?.Email;

    /// Вошли или вышли (поток любой).
    public event Action? SessionChanged;

    // ---------- вход ----------

    /// Почта и пароль (как на сайте).
    public async Task SignInPassword(string email, string password)
    {
        var json = await Auth("token?grant_type=password", new JsonObject { ["email"] = email.Trim(), ["password"] = password }, token: null).ConfigureAwait(false);
        SetSession(ParseSession(json));
    }

    /// Google через браузер. Ждёт, пока браузер вернётся на 127.0.0.1, не дольше 5 минут.
    public async Task SignInGoogle(Action<string> openBrowser, CancellationToken cancel)
    {
        var (verifier, challenge) = Pkce();
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, LoginPort);
            listener.Start();
        }
        catch (SocketException)
        {
            throw new KanbanException($"Порт {LoginPort} занят другой программой: вход через Google сейчас не получится. Войди почтой и паролем.");
        }
        try
        {
            openBrowser(AuthorizeUrl(challenge));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            string code;
            try { code = await WaitForCode(listener, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new KanbanException("Браузер не вернул вход за 5 минут. Если после Google открылся сайт канбана, оболочке нужен адрес возврата в Supabase (Redirect URLs). Пока можно войти почтой и паролем.");
            }
            var json = await Auth("token?grant_type=pkce", new JsonObject { ["auth_code"] = code, ["code_verifier"] = verifier }, token: null).ConfigureAwait(false);
            SetSession(ParseSession(json));
        }
        finally
        {
            listener.Stop();
        }
    }

    public static string AuthorizeUrl(string challenge) =>
        $"{Url}/auth/v1/authorize?provider=google&redirect_to={Uri.EscapeDataString(RedirectUrl)}&code_challenge={challenge}&code_challenge_method=s256";

    /// Пара PKCE: случайный verifier и его SHA-256 в base64url.
    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// Ждём запрос браузера `GET /?code=…` и отвечаем страничкой «Готово». Чужие запросы
    /// (favicon) получают 404 и не мешают.
    public static async Task<string> WaitForCode(TcpListener listener, CancellationToken cancel)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
            client.ReceiveTimeout = 5000;
            var stream = client.GetStream();
            var line = await ReadLine(stream, cancel).ConfigureAwait(false);
            // «GET /?code=abc&state=… HTTP/1.1»
            var parts = line.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "";
            var query = target.Contains('?') ? Query(target[(target.IndexOf('?') + 1)..]) : [];
            if (query.TryGetValue("code", out var code) && code.Length > 0)
            {
                await Respond(stream, 200, "Готово: оболочка вошла в канбан. Вкладку можно закрыть.").ConfigureAwait(false);
                return code;
            }
            if (query.TryGetValue("error_description", out var err) || query.TryGetValue("error", out err))
            {
                await Respond(stream, 200, "Вход не получился: " + err).ConfigureAwait(false);
                throw new KanbanException("Google не дал войти: " + err);
            }
            await Respond(stream, 404, "").ConfigureAwait(false);
        }
    }

    static async Task<string> ReadLine(NetworkStream stream, CancellationToken cancel)
    {
        var buf = new byte[8192];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total), cancel).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            var text = Encoding.ASCII.GetString(buf, 0, total);
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return Encoding.ASCII.GetString(buf, 0, total);
    }

    static Dictionary<string, string> Query(string q)
    {
        var d = new Dictionary<string, string>();
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    static async Task Respond(NetworkStream stream, int status, string text)
    {
        var html = status == 200
            ? $"<!doctype html><meta charset=utf-8><title>Windows Shell</title><body style=\"font:16px 'Segoe UI',sans-serif;background:#1b1d21;color:#f3f4f6;display:grid;place-items:center;height:90vh\"><p>{WebUtility.HtmlEncode(text)}</p>"
            : "";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// Выйти: забыть токены (и сказать Supabase, если он доступен).
    public async Task SignOut()
    {
        var s = _session;
        SetSession(null);
        if (s is null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Url}/auth/v1/logout");
            req.Headers.Add("apikey", Key);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Access);
            using var _ = await _http.SendAsync(req).ConfigureAwait(false);
        }
        catch { }
    }

    /// Свежий токен доступа: за минуту до конца срока обновляется.
    async Task<string> Token(bool force = false)
    {
        var s = _session ?? throw new KanbanException("Нужно войти в канбан.", signedOut: true);
        if (!force && s.Expires > DateTimeOffset.UtcNow.AddSeconds(60)) return s.Access;
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            s = _session ?? throw new KanbanException("Нужно войти в канбан.", signedOut: true);
            if (!force && s.Expires > DateTimeOffset.UtcNow.AddSeconds(60)) return s.Access;
            JsonNode? json;
            try { json = await Auth("token?grant_type=refresh_token", new JsonObject { ["refresh_token"] = s.Refresh }, token: null).ConfigureAwait(false); }
            catch (KanbanException e) when (e.SignedOut)
            {
                Log.Write("канбан: вход больше не действует, нужно войти снова");
                SetSession(null);
                throw;
            }
            var fresh = ParseSession(json);
            SetSession(fresh);
            return fresh.Access;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    async Task<JsonNode?> Auth(string path, JsonObject body, string? token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Url}/auth/v1/{path}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("apikey", Key);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new KanbanException("Нет связи с канбаном. Проверь интернет.");
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return JsonNode.Parse(text);
            var msg = ErrorText(text);
            Log.Write($"канбан: вход {path.Split('?')[^1]} → {(int)res.StatusCode} {msg}");
            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (path.Contains("grant_type=password"))
                    throw new KanbanException(msg.Contains("Invalid login", StringComparison.OrdinalIgnoreCase) ? "Почта или пароль не подходят." : "Не вошли: " + msg);
                if (path.Contains("grant_type=pkce")) throw new KanbanException("Вход через Google не завершился: " + msg);
                throw new KanbanException("Вход больше не действует, войди снова.", signedOut: true);
            }
            throw new KanbanException($"Канбан ответил ошибкой {(int)res.StatusCode}: {msg}");
        }
    }

    static string ErrorText(string json)
    {
        try
        {
            var n = JsonNode.Parse(json);
            return (n?["error_description"] ?? n?["msg"] ?? n?["message"] ?? n?["error"])?.ToString() ?? json;
        }
        catch { return json.Length > 200 ? json[..200] : json; }
    }

    static KSession ParseSession(JsonNode? json)
    {
        var access = json?["access_token"]?.GetValue<string>();
        var refresh = json?["refresh_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh)) throw new KanbanException("Supabase не выдал вход.");
        var expires = json?["expires_at"] is JsonValue at && at.TryGetValue<long>(out var sec)
            ? DateTimeOffset.FromUnixTimeSeconds(sec)
            : DateTimeOffset.UtcNow.AddSeconds(json?["expires_in"] is JsonValue ein && ein.TryGetValue<int>(out var n) ? n : 3600);
        var email = json?["user"]?["email"]?.GetValue<string>() ?? "";
        return new KSession(access, refresh, expires, email);
    }

    void SetSession(KSession? s)
    {
        var was = _session != null;
        _session = s;
        SaveSession(s);
        if (was != (s != null)) Log.Write(s != null ? "канбан: вошли" : "канбан: вышли");
        SessionChanged?.Invoke();
    }

    // ---------- хранение входа (DPAPI) ----------

    static KSession? LoadSession()
    {
        try
        {
            if (!File.Exists(SessionFile)) return null;
            var plain = Unprotect(File.ReadAllBytes(SessionFile));
            return plain is null ? null : JsonSerializer.Deserialize<KSession>(plain);
        }
        catch (Exception e)
        {
            Log.Write($"канбан: вход не прочитан: {e.Message}");
            return null;
        }
    }

    static void SaveSession(KSession? s)
    {
        try
        {
            if (s is null) { if (File.Exists(SessionFile)) File.Delete(SessionFile); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
            var tmp = SessionFile + ".tmp";
            File.WriteAllBytes(tmp, Protect(JsonSerializer.SerializeToUtf8Bytes(s)));
            File.Move(tmp, SessionFile, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Write($"канбан: вход не сохранён: {e.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptProtectData(ref DataBlob input, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref DataBlob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    /// Зашифровать для текущего пользователя Windows.
    public static byte[] Protect(byte[] plain) => Crypt(plain, protect: true) ?? throw new KanbanException("Не удалось зашифровать вход.");

    public static byte[]? Unprotect(byte[] data) => Crypt(data, protect: false);

    static byte[]? Crypt(byte[] data, bool protect)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, "WindowsShell kanban", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) return null;
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    // ---------- таблицы ----------

    async Task<JsonNode?> Rest(HttpMethod method, string path, JsonNode? body = null, string? prefer = null, bool retried = false)
    {
        var token = await Token(force: retried).ConfigureAwait(false);
        using var req = new HttpRequestMessage(method, $"{Url}/rest/v1/{path}");
        req.Headers.Add("apikey", Key);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (prefer != null) req.Headers.Add("Prefer", prefer);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new KanbanException("Нет связи с канбаном. Проверь интернет.");
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text);
            // Токен отозвали раньше срока: один раз обновляем и повторяем.
            if (res.StatusCode == HttpStatusCode.Unauthorized && !retried) return await Rest(method, path, body, prefer, retried: true).ConfigureAwait(false);
            var msg = ErrorText(text);
            Log.Write($"канбан: {method} {path.Split('?')[0]} → {(int)res.StatusCode} {msg}");
            if (res.StatusCode == HttpStatusCode.Conflict) throw new KanbanException(msg, conflict: true);
            throw new KanbanException($"Канбан ответил ошибкой {(int)res.StatusCode}: {msg}");
        }
    }

    async Task<List<T>> All<T>(string table, string query)
    {
        const int page = 1000;
        var list = new List<T>();
        for (var offset = 0; ; offset += page)
        {
            var json = await Rest(HttpMethod.Get, $"{table}?{query}&limit={page}&offset={offset}").ConfigureAwait(false);
            var chunk = json?.Deserialize<List<T>>(Json) ?? [];
            list.AddRange(chunk);
            if (chunk.Count < page) return list;
        }
    }

    public Task<List<KProject>> Projects() => All<KProject>("projects", "select=id,name,prefix,position,archived_at&archived_at=is.null&order=position,name");

    public Task<List<KEpic>> Epics() => All<KEpic>("epics", "select=id,seq,project_id,title,status,position&status=neq.archived&order=project_id,seq");

    /// Все задачи без описаний (вместе с архивом: он нужен для прогресса).
    public Task<List<KItem>> Items() => All<KItem>("items", "select=id,seq,project_id,epic_id,type,title,status,position,created_by,updated_at,closed_at,archived_at&order=id");

    public async Task<KDetails> Details(string id)
    {
        var json = await Rest(HttpMethod.Get, $"items?select=body,checklist&id=eq.{Uri.EscapeDataString(id)}").ConfigureAwait(false);
        return json?.Deserialize<List<KDetails>>(Json)?.FirstOrDefault() ?? new KDetails();
    }

    public async Task<List<KComment>> Comments(string id)
    {
        var json = await Rest(HttpMethod.Get, $"comments?select=id,item_id,author,body,created_at&item_id=eq.{Uri.EscapeDataString(id)}&order=created_at").ConfigureAwait(false);
        return json?.Deserialize<List<KComment>>(Json) ?? [];
    }

    public async Task<KComment> AddComment(string id, string body)
    {
        var json = await Rest(HttpMethod.Post, "comments", new JsonObject { ["item_id"] = id, ["author"] = "me", ["body"] = body }, "return=representation").ConfigureAwait(false);
        return json?.Deserialize<List<KComment>>(Json)?.FirstOrDefault() ?? throw new KanbanException("Комментарий не сохранился.");
    }

    /// Новая задача в бэклог: номер выдаёт база (next_seq, как у сайта), место — в конце.
    public async Task<KItem> Create(KProject project, string title, string? epicId)
    {
        for (var attempt = 0; ; attempt++)
        {
            var seqJson = await Rest(HttpMethod.Post, "rpc/next_seq", new JsonObject { ["p_project"] = project.Id, ["p_kind"] = "item" }).ConfigureAwait(false);
            var seq = seqJson?.GetValue<int>() ?? throw new KanbanException("База не выдала номер задачи.");
            var body = new JsonObject
            {
                ["id"] = $"{project.Prefix}-{seq}",
                ["seq"] = seq,
                ["project_id"] = project.Id,
                ["epic_id"] = epicId,
                ["type"] = "task",
                ["title"] = title,
                ["status"] = KStatus.Backlog,
                ["position"] = seq * 100.0,
                ["created_by"] = "me",
            };
            try
            {
                var json = await Rest(HttpMethod.Post, "items?select=id,seq,project_id,epic_id,type,title,status,position,created_by,updated_at,closed_at,archived_at", body, "return=representation").ConfigureAwait(false);
                return json?.Deserialize<List<KItem>>(Json)?.FirstOrDefault() ?? throw new KanbanException("Задача не сохранилась.");
            }
            // Тот же номер в ту же секунду взяли на сайте: берём следующий.
            catch (KanbanException e) when (e.Conflict && attempt < 2) { }
        }
    }

    /// Изменить поля задачи (status, position, closed_at, title, body, epic_id, checklist).
    public Task Update(string id, JsonObject fields) => Rest(new HttpMethod("PATCH"), $"items?id=eq.{Uri.EscapeDataString(id)}", fields, "return=minimal");
}
