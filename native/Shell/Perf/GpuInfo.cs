using System.Runtime.InteropServices;

namespace WindowsShell.Perf;

/// Видеокарта: имя, объём видеопамяти и LUID — из DXGI; температура — из ядра графики
/// (D3DKMT, то же, что показывает Диспетчер задач; драйверы WDDM 2.9+ NVIDIA, AMD, Intel).
internal sealed class GpuInfo
{
    public required string Name { get; init; }
    public required ulong Vram { get; init; }
    public required uint LuidLow { get; init; }
    public required int LuidHigh { get; init; }

    /// Так LUID пишут счётчики Windows: `luid_0x00000000_0x0000D1B5`.
    public string PdhLuid => $"luid_0x{LuidHigh:X8}_0x{LuidLow:X8}";

    uint _adapter;
    bool _noTemp;

    /// Все видеокарты (без программной), самая «дискретная» (больше видеопамяти) первая.
    public static List<GpuInfo> All()
    {
        var list = new List<GpuInfo>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out var factory) != 0) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                var enumAdapters1 = Method<EnumAdapters1Fn>(factory, 12);
                if (enumAdapters1(factory, i, out var adapter) != 0) break;
                try
                {
                    var desc = new byte[312];
                    var getDesc1 = Method<GetDesc1Fn>(adapter, 10);
                    if (getDesc1(adapter, desc) != 0) continue;
                    var flags = BitConverter.ToUInt32(desc, 304);
                    if ((flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                    var name = System.Text.Encoding.Unicode.GetString(desc, 0, 256).TrimEnd('\0');
                    if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new GpuInfo
                    {
                        Name = name,
                        Vram = BitConverter.ToUInt64(desc, 272),
                        LuidLow = BitConverter.ToUInt32(desc, 296),
                        LuidHigh = BitConverter.ToInt32(desc, 300),
                    });
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return list.GroupBy(g => (g.LuidLow, g.LuidHigh)).Select(g => g.First()).OrderByDescending(g => g.Vram).ToList();
    }

    /// Температура видеокарты, °C (null — драйвер не отдаёт).
    public double? Temperature()
    {
        if (_noTemp) return null;
        if (_adapter == 0)
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { LowPart = LuidLow, HighPart = LuidHigh };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) { _noTemp = true; return null; }
            _adapter = open.hAdapter;
        }
        var data = new D3DKMT_ADAPTER_PERFDATA();
        var size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data, buf, false);
            var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = _adapter, Type = 62 /* KMTQAITYPE_ADAPTERPERFDATA */, pPrivateDriverData = buf, PrivateDriverDataSize = (uint)size };
            var r = D3DKMTQueryAdapterInfo(ref q);
            if (r != 0)
            {
                Log.Write($"видеокарта {Name}: температуру не отдаёт (0x{r:X8})");
                _noTemp = true;
                return null;
            }
            data = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buf);
            // Десятые доли градуса; 0 — датчика нет.
            return data.Temperature is > 0 and < 2000 ? data.Temperature / 10.0 : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static T Method<T>(IntPtr com, int slot) where T : Delegate
    {
        var vtbl = Marshal.ReadIntPtr(com);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc1Fn(IntPtr self, [Out] byte[] desc);

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_OPENADAPTERFROMLUID
    {
        public uint LowPart;
        public int HighPart;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power;
        public ulong Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID open);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO info);
}
