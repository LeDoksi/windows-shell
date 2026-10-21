using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Enumeration;

namespace WindowsShell.Charge;

public enum DeviceKind { Other, Keyboard, Mouse, Headphones, Gamepad }

/// Устройство с батареей. `Key` постоянный между запусками (по нему «не следить» и память
/// об уже показанных уведомлениях). `Percent` null — уровень неизвестен. `Detail` — подробности
/// под именем (у AirPods: левый, правый, футляр).
public sealed record ChargeDevice(string Key, string Name, DeviceKind Kind, int? Percent, bool Charging, bool Connected, string Source, string? Detail = null)
{
    /// «Клавиатура RK-S98» для уведомления; если тип уже в имени («Xbox Controller») — просто имя.
    public string Title
    {
        get
        {
            var kind = Kind switch
            {
                DeviceKind.Keyboard => "Клавиатура",
                DeviceKind.Mouse => "Мышь",
                DeviceKind.Headphones => "Наушники",
                DeviceKind.Gamepad => "Геймпад",
                _ => "",
            };
            if (kind.Length == 0 || Kind == DeviceKind.Gamepad && Name.Contains("controller", StringComparison.OrdinalIgnoreCase)) return Name;
            return $"{kind} {Name}";
        }
    }

    public string Glyph => Kind switch
    {
        DeviceKind.Keyboard => "",
        DeviceKind.Mouse => "",
        DeviceKind.Headphones => "",
        DeviceKind.Gamepad => "",
        _ => "",
    };

    /// Значок уровня батареи (как у батареи ноутбука): 10 ступеней, при зарядке — с молнией.
    public static string LevelGlyph(int percent, bool charging)
    {
        var step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        if (charging) return char.ConvertFromUtf32(0xE85A + Math.Min(step, 9));
        return step == 10 ? "" : char.ConvertFromUtf32(0xE850 + step);
    }
}

/// Заряд Bluetooth-устройств так, как его показывают «Параметры → Bluetooth и устройства»:
/// Windows кладёт уровень в свойство устройства PnP `{104EA319-…} 2` (BLE — служба батареи,
/// обычный Bluetooth — гарнитуры через HFP: AirPods, наушники). Имя и «подключено» берём у
/// сопряжённого устройства Bluetooth с тем же контейнером (одно физическое устройство).
public static class Bluetooth
{
    static readonly DEVPROPKEY BatteryKey = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    static readonly DEVPROPKEY ContainerKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    static readonly DEVPROPKEY FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    static readonly DEVPROPKEY DescKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);
    static readonly DEVPROPKEY ClassKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 10);

    static readonly Guid KeyboardClass = new("4D36E96B-E325-11CE-BFC1-08002BE10318");
    static readonly Guid MouseClass = new("4D36E96F-E325-11CE-BFC1-08002BE10318");
    static readonly Guid MediaClass = new("4D36E96C-E325-11CE-BFC1-08002BE10318");
    static readonly Guid AudioEndpointClass = new("C166523C-FE0C-4A94-A586-F1A80CFBBF3E");
    /// Контейнер самого компьютера: встроенное не считаем.
    static readonly Guid ComputerContainer = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    sealed class Node
    {
        public required string Id;
        public Guid Container;
        public Guid Class;
        public string Name = "";
        public int? Battery;
    }

    public sealed record Endpoint(string Name, bool Connected);

    /// Устройства с уровнем заряда. `log` — подробности для самопроверки.
    public static async Task<List<ChargeDevice>> Read(Action<string>? log = null)
    {
        var nodes = Nodes();
        var withBattery = nodes.Where(n => n.Battery != null && n.Container != Guid.Empty && n.Container != ComputerContainer).ToList();
        foreach (var n in withBattery) log?.Invoke($"PnP {n.Battery}% · {n.Name} · {n.Id} · {n.Container}");
        var endpoints = await Endpoints(log);
        var list = new List<ChargeDevice>();
        // AirPods и Beats: заряда у Windows нет, он в их Bluetooth-объявлениях.
        var apple = endpoints.Where(e => e.Value.Connected && AirPods.IsApple(e.Value.Name) && withBattery.All(n => n.Container != e.Key)).ToList();
        AirPods.Listen(apple.Count > 0);
        foreach (var (container, ep) in apple)
        {
            var s = AirPods.Latest;
            log?.Invoke($"AirPods {ep.Name}: {(s is null ? "объявлений пока нет" : $"{s.Detail} · сигнал {s.Rssi} дБм")}");
            list.Add(new ChargeDevice($"bt:{container:D}", ep.Name.Trim(), DeviceKind.Headphones, s?.Percent, s?.Charging ?? false, true, "Bluetooth (объявления Apple)", s?.Detail));
        }
        foreach (var group in withBattery.GroupBy(n => n.Container))
        {
            var container = group.Key;
            var all = nodes.Where(n => n.Container == container).ToList();
            endpoints.TryGetValue(container, out var ep);
            var name = ep?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = all.FirstOrDefault(n => n.Id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) || n.Id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase))?.Name;
            if (string.IsNullOrWhiteSpace(name)) name = group.First().Name;
            // Уровень у нескольких узлов одного устройства одинаковый; если нет — берём меньший (честнее).
            var percent = group.Min(n => n.Battery!.Value);
            list.Add(new ChargeDevice($"bt:{container:D}", name.Trim(), Kind(name, all), Math.Clamp(percent, 0, 100), false, ep?.Connected ?? true, "Bluetooth"));
        }
        return list;
    }

    static DeviceKind Kind(string name, List<Node> nodes)
    {
        bool Has(Guid g) => nodes.Any(n => n.Class == g);
        bool Named(params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Named("controller", "gamepad", "геймпад", "xbox", "dualsense", "dualshock")) return DeviceKind.Gamepad;
        if (Named("mouse", "мышь", "mx master", "mx anywhere")) return DeviceKind.Mouse;
        if (Has(AudioEndpointClass) || Has(MediaClass) || Named("airpods", "buds", "headphone", "headset", "наушник", "гарнитур")) return DeviceKind.Headphones;
        if (Has(KeyboardClass) || Named("keyboard", "клавиатур")) return DeviceKind.Keyboard;
        if (Has(MouseClass)) return DeviceKind.Mouse;
        return DeviceKind.Other;
    }

    /// Сопряжённые устройства Bluetooth (обычные и BLE) по контейнеру: имя и подключено ли.
    static async Task<Dictionary<Guid, Endpoint>> Endpoints(Action<string>? log)
    {
        var map = new Dictionary<Guid, Endpoint>();
        try
        {
            // Только сопряжённые: без этого условия Windows ещё ~30 с ищет новые устройства вокруг.
            const string query = "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")"
                + " AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
            string[] props = ["System.Devices.Aep.IsConnected", "System.Devices.Aep.IsPaired", "System.Devices.Aep.ContainerId"];
            var find = DeviceInformation.FindAllAsync(query, props, DeviceInformationKind.AssociationEndpoint).AsTask();
            if (await Task.WhenAny(find, Task.Delay(8000)) != find)
            {
                log?.Invoke("Bluetooth: список сопряжённых не пришёл за 8 с");
                return map;
            }
            var all = await find;
            foreach (var d in all)
            {
                if (d.Properties.TryGetValue("System.Devices.Aep.IsPaired", out var p) && p is false) continue;
                if (!d.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var c) || c is not Guid container) continue;
                var connected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var v) && v is true;
                log?.Invoke($"Bluetooth {d.Name} · подключено {connected} · {container}");
                // Одно устройство бывает и обычным, и BLE: подключено, если подключено хоть как-то.
                if (map.TryGetValue(container, out var old)) map[container] = old with { Connected = old.Connected || connected };
                else map[container] = new Endpoint(d.Name, connected);
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"Bluetooth: ошибка {e.Message}");
            Log.Error("заряд: список Bluetooth", e);
        }
        return map;
    }

    /// Все подключённые к системе узлы PnP с контейнером, классом, именем и уровнем заряда.
    static List<Node> Nodes()
    {
        var list = new List<Node>();
        if (CM_Get_Device_ID_List_SizeW(out var len, null, CM_GETIDLIST_FILTER_PRESENT) != 0 || len == 0) return list;
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(null, buf, len, CM_GETIDLIST_FILTER_PRESENT) != 0) return list;
        foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CM_Locate_DevNodeW(out var inst, id, 0) != 0) continue;
            var node = new Node { Id = id };
            if (Prop(inst, ContainerKey) is { Length: 16 } c) node.Container = new Guid(c);
            if (node.Container == Guid.Empty) continue;
            if (Prop(inst, ClassKey) is { Length: 16 } k) node.Class = new Guid(k);
            node.Name = Text(Prop(inst, FriendlyNameKey)) ?? Text(Prop(inst, DescKey)) ?? "";
            if (Prop(inst, BatteryKey) is { Length: >= 1 } b) node.Battery = b[0];
            list.Add(node);
        }
        return list;
    }

    static byte[]? Prop(uint inst, DEVPROPKEY key)
    {
        uint size = 0;
        var r = CM_Get_DevNode_PropertyW(inst, ref key, out _, null, ref size, 0);
        if (r != CR_BUFFER_SMALL || size == 0) return null;
        var buf = new byte[size];
        return CM_Get_DevNode_PropertyW(inst, ref key, out _, buf, ref size, 0) == 0 ? buf : null;
    }

    static string? Text(byte[]? b)
    {
        if (b is null) return null;
        var s = Encoding.Unicode.GetString(b).TrimEnd('\0');
        return s.Length > 0 ? s : null;
    }

    const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    const int CR_BUFFER_SMALL = 0x1A;

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint inst, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_PropertyW(uint inst, ref DEVPROPKEY key, out uint type, [Out] byte[]? buffer, ref uint size, uint flags);
}
