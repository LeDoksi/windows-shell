using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WindowsShell.Perf;

/// Одно измерение раз в секунду. null — показать нечего (датчика нет).
public sealed record PerfSample(
    double Cpu,
    double? CpuTemp,
    double? Gpu,
    double? GpuTemp,
    double RamUsed,
    double RamTotal,
    double? VramUsed,
    double? VramTotal,
    double NetDown,
    double NetUp);

/// Датчики компьютера, все без прав администратора: процессор (GetSystemTimes), память,
/// видеокарта (счётчики Windows как в Диспетчере задач + температура из ядра графики),
/// сеть (адаптеры со шлюзом, то есть с интернетом), температура процессора (CpuTemp).
/// Читается в фоновом потоке.
internal sealed class Sensors : IDisposable
{
    const string GpuEngine = @"\GPU Engine(*)\Utilization Percentage";
    const string GpuMemory = @"\GPU Adapter Memory(*)\Dedicated Usage";

    readonly Pdh _pdh = new();
    readonly CpuTemp _cpuTemp;
    readonly GpuInfo? _gpu;
    readonly bool _gpuLoad, _gpuMem;
    ulong _idle, _kernel, _user;
    long _netAt;
    long _rx, _tx;
    List<NetworkInterface> _nics = [];
    long _nicsAt = long.MinValue;

    public string GpuName => _gpu?.Name ?? "";
    public string CpuTempSource => _cpuTemp.Source;

    public Sensors()
    {
        try { _gpu = GpuInfo.All().FirstOrDefault(); }
        catch (Exception e) { Log.Error("видеокарта", e); }
        _gpuLoad = _pdh.Add(GpuEngine);
        _gpuMem = _pdh.Add(GpuMemory);
        _cpuTemp = new CpuTemp(_pdh);
        _pdh.Collect();
        Cpu();
        Net();
    }

    public PerfSample Read()
    {
        _pdh.Collect();
        var cpu = Cpu();
        double? cpuTemp = null, gpu = null, gpuTemp = null, vram = null;
        try { cpuTemp = _cpuTemp.Read(); } catch (Exception e) { Log.Write($"температура ЦП: {e.Message}"); }
        if (_gpu != null)
        {
            var luid = _gpu.PdhLuid;
            if (_gpuLoad)
            {
                // Как Диспетчер задач: по каждому движку сумма всех процессов, загрузка — самый занятый движок.
                var engines = new Dictionary<string, double>();
                foreach (var (name, value) in _pdh.Values(GpuEngine))
                {
                    var at = name.IndexOf(luid, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;
                    var engine = name[at..];
                    engines[engine] = engines.GetValueOrDefault(engine) + value;
                }
                gpu = engines.Count > 0 ? Math.Clamp(engines.Values.Max(), 0, 100) : 0;
            }
            if (_gpuMem)
            {
                var used = _pdh.Values(GpuMemory).Where(x => x.Name.Contains(luid, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Value);
                vram = used;
            }
            try { gpuTemp = _gpu.Temperature(); } catch (Exception e) { Log.Write($"температура ГП: {e.Message}"); }
        }
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        var (down, up) = Net();
        return new PerfSample(cpu, cpuTemp, gpu, gpuTemp,
            mem.ullTotalPhys - mem.ullAvailPhys, mem.ullTotalPhys,
            vram, _gpu is { Vram: > 0 } g ? g.Vram : null,
            down, up);
    }

    double Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var di = idle - _idle;
        var dk = kernel - _kernel;
        var du = user - _user;
        _idle = idle; _kernel = kernel; _user = user;
        var total = dk + du; // время ядра включает простой
        return total == 0 ? 0 : Math.Clamp(100.0 * (total - di) / total, 0, 100);
    }

    /// Байт в секунду: вниз и вверх.
    (double, double) Net()
    {
        var now = Environment.TickCount64;
        if (now - _nicsAt > 10_000)
        {
            _nicsAt = now;
            try
            {
                _nics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                        && n.GetIPProperties().GatewayAddresses.Count > 0)
                    .ToList();
            }
            catch { _nics = []; }
        }
        long rx = 0, tx = 0;
        foreach (var n in _nics)
        {
            try
            {
                var s = n.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            catch { }
        }
        var dt = (now - _netAt) / 1000.0;
        var first = _netAt == 0;
        var down = first || dt <= 0 ? 0 : Math.Max(0, (rx - _rx) / dt);
        var up = first || dt <= 0 ? 0 : Math.Max(0, (tx - _tx) / dt);
        _netAt = now; _rx = rx; _tx = tx;
        return (down, up);
    }

    public void Dispose() => _pdh.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
