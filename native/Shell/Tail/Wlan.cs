using System.Runtime.InteropServices;
using System.Text;

namespace WindowsShell.Tail;

/// Сеть Wi-Fi в списке окошка сети.
public sealed record WifiNetwork(string Ssid, string? Profile, int Signal, bool Secured, bool Connected, Island.Core.WifiSecurity Security = Island.Core.WifiSecurity.Personal);

/// Native Wifi API (wlanapi.dll): список сетей, подключение к знакомой, отключение.
/// В Windows 11 24H2 список сетей отдаётся, только если программам разрешён доступ к
/// местоположению; иначе ошибка 5 — окошко сети подсказывает, где это включить.
public static class Wlan
{
    public sealed record Result(bool HasAdapter, bool LocationDenied, List<WifiNetwork> Networks);

    public static Result List(bool scan)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return new(false, false, []);
        try
        {
            if (Interface(h) is not { } iface) return new(false, false, []);
            if (scan) WlanScan(h, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            var err = WlanGetAvailableNetworkList(h, ref iface, 0, IntPtr.Zero, out var list);
            if (err == 5) return new(true, true, []);
            if (err != 0) return new(true, false, []);
            try
            {
                var count = Marshal.ReadInt32(list);
                var nets = new List<WifiNetwork>();
                for (var i = 0; i < count; i++)
                {
                    var p = list + 8 + i * NetworkSize;
                    var profile = Marshal.PtrToStringUni(p);
                    var ssidLen = Math.Clamp(Marshal.ReadInt32(p, 512), 0, 32);
                    var ssidBytes = new byte[ssidLen];
                    Marshal.Copy(p + 516, ssidBytes, 0, ssidLen);
                    var ssid = Encoding.UTF8.GetString(ssidBytes);
                    var signal = Marshal.ReadInt32(p, 604);
                    var secured = Marshal.ReadInt32(p, 608) != 0;
                    var auth = Marshal.ReadInt32(p, 612);
                    var flags = Marshal.ReadInt32(p, 620);
                    if (string.IsNullOrEmpty(ssid)) continue;
                    var security = secured ? Island.Core.WifiAuth.Of(auth) : Island.Core.WifiSecurity.Open;
                    nets.Add(new WifiNetwork(ssid, (flags & 2) != 0 && !string.IsNullOrEmpty(profile) ? profile : null, signal, secured, (flags & 1) != 0, security));
                }
                // У одной сети бывает две строки: со знакомым профилем и без. Оставляем лучшую.
                var unique = nets
                    .GroupBy(n => n.Ssid)
                    .Select(g => g.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Profile != null).ThenByDescending(n => n.Signal).First())
                    .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal)
                    .ToList();
                return new(true, false, unique);
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// Подключиться к сети с сохранённым профилем. true — Windows приняла просьбу.
    public static bool Connect(string profile)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return false;
        var name = Marshal.StringToHGlobalUni(profile);
        try
        {
            if (Interface(h) is not { } iface) return false;
            var p = new WLAN_CONNECTION_PARAMETERS { wlanConnectionMode = 0, strProfile = name, dot11BssType = 1 };
            return WlanConnect(h, ref iface, ref p, IntPtr.Zero) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            WlanCloseHandle(h, IntPtr.Zero);
        }
    }

    /// Новая сеть (И5): профиль из XML (пароль в нём, в лог не пишется) и подключение.
    /// null — Windows приняла просьбу; иначе текст ошибки для острова.
    public static string? ConnectNew(string ssid, Island.Core.WifiSecurity security, string? password)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return "Wi-Fi недоступен";
        try
        {
            if (Interface(h) is not { } iface) return "Нет Wi-Fi-адаптера";
            var xml = Island.Core.WifiProfile.Xml(ssid, security, password);
            var err = WlanSetProfile(h, ref iface, 0, xml, null, true, IntPtr.Zero, out var reason);
            if (err != 0)
            {
                Log.Write($"сеть: профиль «{ssid}» не принят (ошибка {err}, причина {reason})");
                return "Windows не приняла настройки этой сети";
            }
            var name = Marshal.StringToHGlobalUni(ssid);
            try
            {
                var p = new WLAN_CONNECTION_PARAMETERS { wlanConnectionMode = 0, strProfile = name, dot11BssType = 1 };
                var c = WlanConnect(h, ref iface, ref p, IntPtr.Zero);
                if (c != 0) { Log.Write($"сеть: подключение к «{ssid}» не началось (ошибка {c})"); return "Не удалось начать подключение"; }
                return null;
            }
            finally { Marshal.FreeHGlobal(name); }
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// Убрать профиль, созданный островом, если подключиться не вышло (неверный пароль).
    public static void DeleteProfile(string name)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return;
        try { if (Interface(h) is { } iface) WlanDeleteProfile(h, ref iface, name, IntPtr.Zero); }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    public static void Disconnect()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return;
        try { if (Interface(h) is { } iface) WlanDisconnect(h, ref iface, IntPtr.Zero); }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    static Guid? Interface(IntPtr h)
    {
        if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return null;
        try
        {
            if (Marshal.ReadInt32(list) == 0) return null;
            // WLAN_INTERFACE_INFO: GUID, описание (256 символов), состояние.
            var bytes = new byte[16];
            Marshal.Copy(list + 8, bytes, 0, 16);
            return new Guid(bytes);
        }
        finally { WlanFreeMemory(list); }
    }

    // WLAN_AVAILABLE_NETWORK: профиль 512, SSID 36 (+512), тип 4, BSSID 4, connectable 4,
    // причина 4, число PHY 4, PHY 32, ещё PHY 4, качество +604, защита +608, алгоритмы 8, флаги +620, резерв 4.
    const int NetworkSize = 628;

    [StructLayout(LayoutKind.Sequential)]
    struct WLAN_CONNECTION_PARAMETERS
    {
        public int wlanConnectionMode;
        public IntPtr strProfile, pDot11Ssid, pDesiredBssidList;
        public int dot11BssType;
        public uint dwFlags;
    }

    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr h, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr h, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanScan(IntPtr h, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanGetAvailableNetworkList(IntPtr h, ref Guid iface, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanConnect(IntPtr h, ref Guid iface, ref WLAN_CONNECTION_PARAMETERS p, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern uint WlanDisconnect(IntPtr h, ref Guid iface, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern uint WlanSetProfile(IntPtr h, ref Guid iface, uint flags, string xml, string? security, bool overwrite, IntPtr reserved, out uint reason);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern uint WlanDeleteProfile(IntPtr h, ref Guid iface, string name, IntPtr reserved);
}
