using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using WindowsShell.Charge;

namespace WindowsShell.Tail;

/// Сопряжённое Bluetooth-устройство (одно физическое = один контейнер PnP).
public sealed record BtDevice(Guid Container, string Name, DeviceKind Kind, bool Connected, ulong Address, bool Classic, bool Le, bool Audio, List<string> AudioFilters)
{
    public string Key => Container.ToString("D");
    public string Glyph => new ChargeDevice("", "", Kind, null, false, false, "").Glyph;
}

/// Найденное при поиске (ещё не сопряжено).
public sealed record BtFound(string Id, string Name, bool Le);

/// Bluetooth в острове (И4, WSH-163). Список сопряжённых — из узлов PnP, как у заряда (быстро;
/// WinRT-список на mephPC идёт 30 с и дольше). Подключить/отключить наушники по кнопке —
/// чистого API нет; тот же приём, что у утилит вроде ToothTray: свойство KS
/// `KSPROPSETID_BtAudio` (ONESHOT_RECONNECT / ONESHOT_DISCONNECT) у аудиофильтров этого
/// устройства. Сопряжение новых — `DeviceInformationCustomPairing` с PIN в острове.
public static class BtDevices
{
    static readonly Guid KsCategoryAudio = new("6994AD04-93EF-11D0-A3CC-00A0C9223196");
    static readonly Guid KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    const uint KSPROPERTY_ONESHOT_RECONNECT = 0, KSPROPERTY_ONESHOT_DISCONNECT = 1, KSPROPERTY_TYPE_GET = 1;
    const uint IOCTL_KS_PROPERTY = 0x2F0003;

    /// Сопряжённые устройства. `log` — подробности для `--bt-test`.
    public static List<BtDevice> List(Action<string>? log = null)
    {
        var nodes = Bluetooth.Nodes();
        var list = new List<BtDevice>();
        foreach (var g in nodes.Where(n => n.IsDevice && n.Container != Bluetooth.ComputerContainer).GroupBy(n => n.Container))
        {
            var dev = g.ToList();
            var all = nodes.Where(n => n.Container == g.Key).ToList();
            var name = dev.Select(n => n.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))?.Trim() ?? "Устройство Bluetooth";
            var connected = dev.Any(n => n.Connected == true);
            var address = dev.Select(n => Address(n.Id)).FirstOrDefault(a => a != 0);
            var classic = dev.Any(n => n.Id.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase));
            var le = dev.Any(n => n.Id.StartsWith(@"BTHLE\", StringComparison.OrdinalIgnoreCase));
            var filters = all.SelectMany(n => Interfaces(n.Id, KsCategoryAudio)).Distinct().ToList();
            var audio = filters.Count > 0 || all.Any(n => n.Class == Bluetooth.MediaClass || n.Class == Bluetooth.AudioEndpointClass);
            var kind = Bluetooth.Kind(name, all);
            list.Add(new BtDevice(g.Key, name, kind, connected, address, classic, le, audio, filters));
            log?.Invoke($"{name} · {kind} · {(connected ? "подключено" : "не подключено")} · адрес {address:X12} · {(classic ? "обычный " : "")}{(le ? "BLE " : "")}· аудио {(audio ? "да" : "нет")}, KS-фильтров {filters.Count}");
        }
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// `BTHENUM\DEV_001122AABBCC\…` → адрес.
    static ulong Address(string id)
    {
        var i = id.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || id.Length < i + 16) return 0;
        return ulong.TryParse(id.AsSpan(i + 4, 12), System.Globalization.NumberStyles.HexNumber, null, out var a) ? a : 0;
    }

    /// Подключить (true) или отключить (false) аудиоустройство. null — получилось; иначе причина.
    public static string? SetConnected(BtDevice d, bool on)
    {
        if (d.AudioFilters.Count == 0) return "У этого устройства нет звука — Windows подключает его сама.";
        var ok = 0;
        var errors = new List<string>();
        foreach (var path in d.AudioFilters)
        {
            var e = KsCall(path, on ? KSPROPERTY_ONESHOT_RECONNECT : KSPROPERTY_ONESHOT_DISCONNECT);
            if (e == 0) ok++;
            else errors.Add($"0x{e:X}");
        }
        Log.Write($"Bluetooth: {(on ? "подключить" : "отключить")} «{d.Name}» — фильтров {d.AudioFilters.Count}, успешно {ok}{(errors.Count > 0 ? $", ошибки {string.Join(", ", errors.Distinct())}" : "")}");
        return ok > 0 ? null : on ? "Не подключилось. Включи наушники и проверь, что они рядом." : "Не отключилось.";
    }

    /// Свойство KS без данных (GET). 0 — получилось, иначе код ошибки Win32.
    static int KsCall(string path, uint id)
    {
        var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h == new IntPtr(-1)) h = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (h == new IntPtr(-1)) return Marshal.GetLastWin32Error();
        try
        {
            var prop = new KSPROPERTY { Set = KsPropSetBtAudio, Id = id, Flags = KSPROPERTY_TYPE_GET };
            return DeviceIoControl(h, IOCTL_KS_PROPERTY, ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();
        }
        finally { CloseHandle(h); }
    }

    /// Удалить устройство (разорвать сопряжение). null — получилось.
    public static async Task<string?> Remove(BtDevice d)
    {
        try
        {
            DeviceInformation? info = null;
            if (d.Classic && await BluetoothDevice.FromBluetoothAddressAsync(d.Address) is { } c) info = c.DeviceInformation;
            if (info is null && d.Le && await BluetoothLEDevice.FromBluetoothAddressAsync(d.Address) is { } l) info = l.DeviceInformation;
            if (info is null) return "Windows не нашла это устройство. Удали его в параметрах Bluetooth.";
            var r = await info.Pairing.UnpairAsync();
            Log.Write($"Bluetooth: удалить «{d.Name}» — {r.Status}");
            return r.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired ? null : $"Не удалилось ({r.Status}).";
        }
        catch (Exception e)
        {
            Log.Error("Bluetooth: удалить", e);
            return "Не удалилось. Удали его в параметрах Bluetooth.";
        }
    }

    /// Поиск новых устройств (обычных и BLE). Остановить — Dispose результата.
    public static IDisposable Scan(Action<BtFound> added, Action<string> removed)
    {
        var watchers = new List<DeviceWatcher>();
        foreach (var (aqs, le) in new[] { (BluetoothDevice.GetDeviceSelectorFromPairingState(false), false), (BluetoothLEDevice.GetDeviceSelectorFromPairingState(false), true) })
        {
            try
            {
                var w = DeviceInformation.CreateWatcher(aqs, null, DeviceInformationKind.AssociationEndpoint);
                w.Added += (_, i) => { if (!string.IsNullOrWhiteSpace(i.Name) && i.Pairing.CanPair) added(new BtFound(i.Id, i.Name.Trim(), le)); };
                w.Removed += (_, u) => removed(u.Id);
                w.Start();
                watchers.Add(w);
            }
            catch (Exception e) { Log.Error("Bluetooth: поиск", e); }
        }
        Log.Write("Bluetooth: поиск новых устройств начат");
        return new Stopper(watchers);
    }

    sealed class Stopper(List<DeviceWatcher> watchers) : IDisposable
    {
        public void Dispose()
        {
            foreach (var w in watchers)
                try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch { }
            watchers.Clear();
        }
    }

    /// Вопрос при сопряжении: что показать и что ответить. `Pin` — PIN устройства (показать
    /// или сверить); для ProvidePin — null, ответ — введённый PIN.
    public sealed record PairAsk(DevicePairingKinds Kind, string? Pin);

    /// Сопрячь. `ask` — спросить человека (в острове); ответ (ok, pin). null — сопряжено; иначе причина.
    public static async Task<string?> Pair(string id, Func<PairAsk, Task<(bool Ok, string? Pin)>> ask)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(id);
            var custom = info.Pairing.Custom;
            async void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs a)
            {
                var deferral = a.GetDeferral();
                try
                {
                    if (a.PairingKind == DevicePairingKinds.ConfirmOnly) { a.Accept(); return; }
                    var question = ask(new PairAsk(a.PairingKind, a.PairingKind == DevicePairingKinds.ProvidePin ? null : a.Pin));
                    // Не ответили за минуту — отказ (иначе сопряжение висит, а Windows ждёт).
                    var done = await Task.WhenAny(question, Task.Delay(60_000));
                    if (done != question) { Log.Write("Bluetooth: сопряжение — нет ответа за минуту"); return; }
                    var (ok, pin) = question.Result;
                    if (!ok) return;
                    if (a.PairingKind == DevicePairingKinds.ProvidePin) a.Accept(pin ?? "");
                    else a.Accept();
                }
                catch (Exception e) { Log.Error("Bluetooth: вопрос сопряжения", e); }
                finally { deferral.Complete(); }
            }
            custom.PairingRequested += OnRequested;
            try
            {
                var kinds = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ProvidePin;
                var r = await custom.PairAsync(kinds, DevicePairingProtectionLevel.Default);
                Log.Write($"Bluetooth: сопряжение «{info.Name}» — {r.Status}");
                return r.Status switch
                {
                    DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired => null,
                    DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.ConnectionRejected or DevicePairingResultStatus.RejectedByHandler or DevicePairingResultStatus.PairingCanceled => "Сопряжение отменено.",
                    DevicePairingResultStatus.AuthenticationFailure => "PIN не подошёл. Попробуй ещё раз.",
                    DevicePairingResultStatus.NotReadyToPair or DevicePairingResultStatus.NoSupportedProfiles => "Устройство не готово: переведи его в режим сопряжения и попробуй снова.",
                    _ => $"Не получилось ({r.Status}). Попробуй в параметрах Bluetooth.",
                };
            }
            finally { custom.PairingRequested -= OnRequested; }
        }
        catch (Exception e)
        {
            Log.Error("Bluetooth: сопряжение", e);
            return "Не получилось. Попробуй в параметрах Bluetooth.";
        }
    }

    // ---------- интерфейсы устройств ----------

    static List<string> Interfaces(string deviceId, Guid category)
    {
        var cls = category;
        if (CM_Get_Device_Interface_List_SizeW(out var len, ref cls, deviceId, 0 /* только включённые */) != 0 || len <= 1) return [];
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref cls, deviceId, buf, len, 0) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// `--bt-test`: список устройств и найденных аудиофильтров; ничего не переключает. Итог — `bt-test.txt`.
    public const string Arg = "--bt-test";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проверка Bluetooth: {line}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var list = List(Say);
            Say($"сопряжённых {list.Count}, из них со звуком {list.Count(d => d.Audio)}, прочитано за {sw.ElapsedMilliseconds} мс");
            foreach (var d in list.Where(d => d.Audio))
                foreach (var f in d.AudioFilters) Say($"   {d.Name}: фильтр {f}");
            var found = new List<BtFound>();
            using (Scan(f => { lock (found) found.Add(f); }, _ => { }))
                Thread.Sleep(8000);
            Say($"поиск 8 с: найдено новых {found.Select(f => f.Id).Distinct().Count()}");
        }
        catch (Exception e) { Say($"ОШИБКА: {e}"); }
        Say("ГОТОВО");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "bt-test.txt"), report.ToString());
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KSPROPERTY
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid cls, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string? deviceId, [Out] char[] buffer, uint len, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, ref KSPROPERTY input, uint inSize, IntPtr output, uint outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
