using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WindowsShell.Charge;

/// Заряд устройств Logitech (гарнитура G733 и др.) из G HUB — неофициально: окно G HUB
/// говорит со своей службой по `ws://localhost:9010`, мы слушаем то же самое (так делают
/// LGSTrayBattery и подобные). G HUB не запущен или не отвечает — устройств из него просто нет.
public sealed class GHub
{
    static readonly Uri Address = new("ws://localhost:9010");

    sealed class Dev
    {
        public required string Id;
        public string Name = "";
        public string Type = "";
        public bool Battery;
        public bool Connected = true;
        public int? Percent;
        public bool Charging;
    }

    readonly Dictionary<string, Dev> _devs = [];
    readonly object _lock = new();
    readonly Action<string>? _log;
    bool _running;

    /// Список или заряд поменялись (зовётся из фонового потока).
    public event Action? Changed;

    public GHub(Action<string>? log = null) => _log = log;

    public bool Connected { get; private set; }

    public List<ChargeDevice> Devices
    {
        get
        {
            lock (_lock)
                return _devs.Values.Where(d => d.Battery && d.Percent != null)
                    .Select(d => new ChargeDevice($"ghub:{d.Name}", d.Name, Kind(d), d.Percent, d.Charging, d.Connected, "G HUB")).ToList();
        }
    }

    static DeviceKind Kind(Dev d)
    {
        var t = d.Type.ToLowerInvariant();
        if (t.Contains("headset") || t.Contains("audio")) return DeviceKind.Headphones;
        if (t.Contains("keyboard")) return DeviceKind.Keyboard;
        if (t.Contains("mouse")) return DeviceKind.Mouse;
        var n = d.Name.ToLowerInvariant();
        return n.Contains("headset") || n.StartsWith("g733") || n.Contains("g733") ? DeviceKind.Headphones : DeviceKind.Other;
    }

    /// Держать соединение в фоне: обрыв — повтор через 30 с.
    public void Start()
    {
        if (_running) return;
        _running = true;
        new Thread(() => Loop().GetAwaiter().GetResult()) { IsBackground = true, Name = "ghub" }.Start();
    }

    async Task Loop()
    {
        var quiet = false;
        while (true)
        {
            try
            {
                await Session(TimeSpan.MaxValue);
            }
            catch (Exception e)
            {
                // В журнал — только первый раз подряд: G HUB может быть не установлен вовсе.
                if (!quiet) Log.Write($"заряд: G HUB не отвечает ({e.Message})");
                quiet = true;
            }
            if (Connected) quiet = false;
            Connected = false;
            bool had;
            lock (_lock) { had = _devs.Count > 0; _devs.Clear(); }
            if (had) Changed?.Invoke();
            await Task.Delay(30_000);
        }
    }

    /// Одно соединение: список устройств, подписка на заряд, раз в минуту переспросить уровень.
    /// `limit` — для самопроверки (несколько секунд и выйти).
    public async Task Session(TimeSpan limit)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("json");
        ws.Options.SetRequestHeader("Origin", "file://");
        using (var connect = new CancellationTokenSource(3000))
            await ws.ConnectAsync(Address, connect.Token);
        Connected = true;
        Log.Write("заряд: G HUB подключён");
        using var cts = limit == TimeSpan.MaxValue ? new CancellationTokenSource() : new CancellationTokenSource(limit);
        using var sending = new SemaphoreSlim(1);
        async Task Send(string verb, string path)
        {
            var json = JsonSerializer.Serialize(new { msgId = "", verb, path });
            await sending.WaitAsync();
            try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { sending.Release(); }
        }
        async Task AskBattery()
        {
            List<string> ids;
            lock (_lock) ids = _devs.Values.Where(d => d.Battery).Select(d => d.Id).ToList();
            foreach (var id in ids) await Send("GET", $"/battery/{id}/state");
        }
        await Send("GET", "/devices/list");
        await Send("SUBSCRIBE", "/devices/state/changed");
        await Send("SUBSCRIBE", "/battery/state/changed");
        // Подписка присылает изменения, но не всегда: раз в минуту спрашиваем сами.
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(60_000, cts.Token);
                    await AskBattery();
                }
            }
            catch { }
        });
        var buf = new byte[1 << 16];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                var text = Encoding.UTF8.GetString(ms.ToArray());
                _log?.Invoke($"G HUB << {(text.Length > 2000 ? text[..2000] + "…" : text)}");
                var (changed, ask, relist) = Handle(text);
                if (changed) Changed?.Invoke();
                if (ask) await AskBattery();
                if (relist) await Send("GET", "/devices/list");
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally { cts.Cancel(); }
    }

    /// Разобрать ответ G HUB: поменялось ли что-то, переспросить заряд, переспросить список.
    (bool Changed, bool Ask, bool Relist) Handle(string text)
    {
        JsonNode? msg;
        try { msg = JsonNode.Parse(text); }
        catch { return default; }
        var path = Str(msg?["path"]) ?? "";
        var payload = msg?["payload"];
        if (payload is null) return default;
        if (path == "/devices/list")
        {
            if (payload["deviceInfos"] is not JsonArray infos) return default;
            lock (_lock)
            {
                var seen = new HashSet<string>();
                foreach (var info in infos)
                {
                    var id = Str(info?["id"]);
                    if (id is null) continue;
                    seen.Add(id);
                    if (!_devs.TryGetValue(id, out var d)) _devs[id] = d = new Dev { Id = id };
                    d.Name = Str(info?["extendedDisplayName"]) ?? Str(info?["displayName"]) ?? Str(info?["name"]) ?? id;
                    d.Type = Str(info?["deviceType"]) ?? "";
                    d.Battery = info?["capabilities"]?["hasBatteryStatus"] is JsonValue hb && hb.TryGetValue<bool>(out var b) ? b : d.Battery;
                    if (Str(info?["state"]) is { } state) d.Connected = !state.Contains("NOT_CONNECTED", StringComparison.OrdinalIgnoreCase) && !state.Contains("ABSENT", StringComparison.OrdinalIgnoreCase);
                }
                foreach (var gone in _devs.Keys.Where(k => !seen.Contains(k)).ToList()) _devs.Remove(gone);
            }
            return (true, true, false);
        }
        if (path.StartsWith("/devices/state", StringComparison.Ordinal) || path.StartsWith("/devices/", StringComparison.Ordinal) && path.EndsWith("/state", StringComparison.Ordinal))
        {
            return (false, false, true);
        }
        if (path.StartsWith("/battery/", StringComparison.Ordinal))
        {
            var id = Str(payload["deviceId"]) ?? path.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
            if (id is null || !TryInt(payload["percentage"], out var percent)) return default;
            var charging = payload["charging"] is JsonValue cv && cv.TryGetValue<bool>(out var c) && c;
            var relist = false;
            lock (_lock)
            {
                if (!_devs.TryGetValue(id, out var d)) { _devs[id] = d = new Dev { Id = id, Name = id }; relist = true; }
                d.Battery = true;
                if (d.Percent == percent && d.Charging == charging) return (false, false, relist);
                d.Percent = Math.Clamp(percent, 0, 100);
                d.Charging = charging;
            }
            return (true, false, relist);
        }
        return default;
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool TryInt(JsonNode? n, out int value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<int>(out value)) return true;
        if (v.TryGetValue<double>(out var d)) { value = (int)Math.Round(d); return true; }
        return false;
    }
}
