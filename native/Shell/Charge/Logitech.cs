using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsShell.Charge;

/// Заряд устройств Logitech напрямую, без G HUB (гарнитура G733 на своём приёмнике, мыши и
/// клавиатуры на Lightspeed/Unifying): говорим с ними по их протоколу HID++ 2.0, как Solaar и
/// HeadsetControl. Спрашиваем у устройства, какие «функции» у него есть, и берём первую из:
/// 0x1004 (заряд в %), 0x1000 (заряд в %), 0x1F20 (напряжение → % по кривой Li-ion).
public static class Logitech
{
    const ushort Vid = 0x046D;
    const byte Long = 0x11, SoftwareId = 0x0B;

    /// Все устройства Logitech с батареей. `log` — подробности для самопроверки.
    public static List<ChargeDevice> Read(Action<string>? log = null)
    {
        var list = new List<ChargeDevice>();
        foreach (var path in HidPaths())
        {
            try
            {
                using var dev = Open(path, out var pid, out var caps, out var name);
                if (dev is null) continue;
                // Длинные сообщения HID++ — 20 байт в обе стороны; остальные коллекции не наши.
                if (caps.OutputReportByteLength != 20 || caps.InputReportByteLength != 20) continue;
                log?.Invoke($"Logitech {pid:X4} {name} · коллекция {caps.UsagePage:X4}:{caps.Usage:X4}");
                using var stream = new FileStream(dev, FileAccess.ReadWrite, 1, isAsync: true);
                // Устройство на своём приёмнике (гарнитура) отвечает как 0xFF; приёмник для
                // нескольких устройств (Unifying, Lightspeed) — номерами 1…6.
                var before = list.Count;
                foreach (byte index in new byte[] { 0xFF, 1, 2, 3, 4, 5, 6 })
                {
                    var d = Battery(stream, index, pid, name, log, out var silent);
                    if (d != null) list.Add(d);
                    // Нашлось как отдельное устройство — номера приёмника не нужны; молчит
                    // и как устройство, и как приёмник — это не HID++.
                    if (index == 0xFF && d != null || index == 1 && silent && list.Count == before) break;
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"Logitech {path}: {e.Message}");
            }
        }
        // Одно устройство может быть видно через несколько коллекций.
        return list.GroupBy(d => d.Key).Select(g => g.First()).ToList();
    }

    /// `silent` — на этом номере вообще никто не ответил (даже ошибкой).
    static ChargeDevice? Battery(FileStream s, byte index, ushort pid, string name, Action<string>? log, out bool silent)
    {
        silent = false;
        foreach (var feature in new ushort[] { 0x1004, 0x1000, 0x1F20 })
        {
            // Root (0x0000), функция 0: номер нужной функции у этого устройства (0 — нет такой).
            var root = Call(s, index, 0x00, 0, [(byte)(feature >> 8), (byte)feature], out var answered);
            if (root is null)
            {
                // Ошибка или тишина на первый же вопрос — на этом номере устройства нет.
                silent = !answered;
                if (feature == 0x1004) return null;
                continue;
            }
            var fi = root[0];
            if (fi == 0) continue;
            int? percent = null;
            var charging = false;
            var connected = true;
            switch (feature)
            {
                case 0x1004:
                {
                    // get_status: заряд %, уровень, состояние (1, 2 — заряжается, 3 — полный).
                    var r = Call(s, index, fi, 1, [], out _);
                    if (r is null) continue;
                    percent = r[0];
                    charging = r[2] is 1 or 2;
                    break;
                }
                case 0x1000:
                {
                    // GetBatteryLevelStatus: заряд %, следующий уровень, состояние (1, 2 — заряжается).
                    var r = Call(s, index, fi, 0, [], out _);
                    if (r is null) continue;
                    percent = r[0];
                    charging = r[2] is 1 or 2;
                    break;
                }
                default:
                {
                    // getADCMeasurement: напряжение (мВ) и флаги: бит 0 — устройство включено, бит 1 — заряжается.
                    var r = Call(s, index, fi, 0, [], out _);
                    if (r is null) continue;
                    var mv = r[0] << 8 | r[1];
                    connected = (r[2] & 1) != 0;
                    charging = (r[2] & 2) != 0;
                    if (connected) percent = FromVoltage(mv);
                    log?.Invoke($"   напряжение {mv} мВ, флаги {r[2]:X2}");
                    break;
                }
            }
            log?.Invoke($"   №{index:X2} функция {feature:X4} → {percent?.ToString() ?? "?"} %, заряжается {charging}, включено {connected}");
            // За приёмником имя спрашиваем у самого устройства (у приёмника имя «USB Receiver»).
            var title = Clean(index == 0xFF ? name : DeviceName(s, index) ?? name);
            return new ChargeDevice($"logi:{pid:X4}:{index:X2}", title, Kind(title), percent is { } p ? Math.Clamp(p, 0, 100) : null, charging, connected, "Logitech");
        }
        return null;
    }

    /// Имя устройства (функция 0x0005): длина, потом кусками по 16 символов.
    static string? DeviceName(FileStream s, byte index)
    {
        var root = Call(s, index, 0x00, 0, [0x00, 0x05], out _);
        if (root is not { } r || r[0] == 0) return null;
        var fi = r[0];
        if (Call(s, index, fi, 0, [], out _) is not { } count || count[0] == 0) return null;
        var bytes = new List<byte>();
        while (bytes.Count < count[0])
        {
            if (Call(s, index, fi, 1, [(byte)bytes.Count], out _) is not { } part) break;
            bytes.AddRange(part.Take(Math.Min(16, count[0] - bytes.Count)));
        }
        var text = System.Text.Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
        return text.Length > 0 ? text : null;
    }

    /// Кривая разряда литиевого аккумулятора (как в Solaar): напряжение → %.
    static readonly (int Mv, int Percent)[] Curve =
        [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0)];

    static int FromVoltage(int mv)
    {
        if (mv >= Curve[0].Mv) return 100;
        for (var i = 1; i < Curve.Length; i++)
        {
            var (hi, ph) = Curve[i - 1];
            var (lo, pl) = Curve[i];
            if (mv >= lo) return pl + (ph - pl) * (mv - lo) / (hi - lo);
        }
        return 0;
    }

    static string Clean(string name)
    {
        var n = name.Replace("Logitech", "", StringComparison.OrdinalIgnoreCase).Trim();
        return n.Length > 0 ? n : name;
    }

    static DeviceKind Kind(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("headset") || n.Contains("g733") || n.Contains("g435") || n.Contains("g535") || n.Contains("pro x")) return DeviceKind.Headphones;
        if (n.Contains("keyboard") || n.Contains("keys")) return DeviceKind.Keyboard;
        if (n.Contains("mouse") || n.Contains("mx ")) return DeviceKind.Mouse;
        return DeviceKind.Other;
    }

    /// Один запрос HID++ 2.0 и ответ на него (параметры ответа, 16 байт). null — нет ответа или ошибка.
    static byte[]? Call(FileStream s, byte index, byte feature, byte function, byte[] args, out bool answered)
    {
        answered = false;
        var msg = new byte[20];
        msg[0] = Long;
        msg[1] = index;
        msg[2] = feature;
        msg[3] = (byte)(function << 4 | SoftwareId);
        args.CopyTo(msg, 4);
        s.Write(msg);
        var buf = new byte[20];
        var until = Environment.TickCount64 + 600;
        while (Environment.TickCount64 < until)
        {
            using var cts = new CancellationTokenSource((int)Math.Max(1, until - Environment.TickCount64));
            int n;
            try { n = s.ReadAsync(buf, cts.Token).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return null; }
            if (n < 5 || buf[1] != index) continue;
            // Ошибка HID++ 2.0: 0xFF, номер функции, запрос, код.
            if (buf[2] == 0xFF && buf[3] == feature && buf[4] == msg[3]) { answered = true; return null; }
            // Ошибка HID++ 1.0 (устройство не на связи): 0x8F.
            if (buf[2] == 0x8F && buf[3] == feature) { answered = true; return null; }
            if (buf[2] == feature && buf[3] == msg[3]) { answered = true; return buf[4..]; }
        }
        return null;
    }

    // ---------- HID ----------

    static IEnumerable<string> HidPaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                if (size == 0) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    // cbSize структуры с путём: 8 на x64.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (path != null && path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)) yield return path;
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    static SafeFileHandle? Open(string path, out ushort pid, out HIDP_CAPS caps, out string name)
    {
        pid = 0;
        caps = default;
        name = "";
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(h, ref attrs) || attrs.VendorID != Vid) { h.Dispose(); return null; }
        pid = attrs.ProductID;
        if (HidD_GetPreparsedData(h, out var pre))
        {
            HidP_GetCaps(pre, out caps);
            HidD_FreePreparsedData(pre);
        }
        var buf = new char[128];
        if (HidD_GetProductString(h, buf, buf.Length * 2)) name = new string(buf).TrimEnd('\0');
        if (name.Length == 0) name = $"Logitech {pid:X4}";
        return h;
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, [Out] char[] buffer, int length);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(ref Guid cls, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
}
