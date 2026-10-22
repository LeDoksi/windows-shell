using System.Runtime.InteropServices;

namespace WindowsShell.Perf;

/// Счётчики производительности Windows (те же, что у Диспетчера задач): загрузка видеокарты по
/// движкам, видеопамять, температура зон ACPI. Один запрос, собирается раз в секунду.
internal sealed class Pdh : IDisposable
{
    readonly IntPtr _query;
    readonly Dictionary<string, IntPtr> _counters = [];

    public Pdh()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    /// Добавить счётчик по английскому пути, например `\GPU Engine(*)\Utilization Percentage`.
    public bool Add(string path)
    {
        if (_query == IntPtr.Zero) return false;
        if (_counters.ContainsKey(path)) return true;
        var r = PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var c);
        if (r != 0)
        {
            Log.Write($"PDH: {path}: 0x{r:X8}");
            return false;
        }
        _counters[path] = c;
        return true;
    }

    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    /// Значения всех экземпляров счётчика: имя экземпляра → значение.
    public List<(string Name, double Value)> Values(string path)
    {
        var list = new List<(string, double)>();
        if (!_counters.TryGetValue(path, out var c)) return list;
        uint size = 0;
        var r = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (r != PDH_MORE_DATA || size == 0) return list;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            r = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buf);
            if (r != 0) return list;
            var item = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            for (var i = 0; i < count; i++)
            {
                var v = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buf + i * item);
                if (v.CStatus is not (0 or 1)) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA
                list.Add((Marshal.PtrToStringUni(v.szName) ?? "", v.doubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
    }

    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public uint CStatus;
        public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
}
