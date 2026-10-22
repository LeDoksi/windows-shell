using System.IO.MemoryMappedFiles;

namespace WindowsShell.Perf;

/// Температура процессора. Windows её напрямую не отдаёт: датчик читается только драйвером
/// ядра. Берём у программ, которые этот драйвер уже держат и делятся показаниями: MSI Afterburner
/// (общая память MAHM). Если таких нет — зона ACPI «Температура» из счётчиков Windows, когда
/// материнская плата отдаёт туда процессор (значение меняется со временем).
internal sealed class CpuTemp
{
    readonly Pdh _pdh;
    const string Zone = @"\Thermal Zone Information(*)\Temperature";
    readonly bool _zone;
    double _zoneMin = double.MaxValue, _zoneMax = double.MinValue;

    /// Откуда взята последняя температура (для настроек и проверки).
    public string Source { get; private set; } = "нет";

    public CpuTemp(Pdh pdh)
    {
        _pdh = pdh;
        _zone = pdh.Add(Zone);
    }

    /// Зовётся после Pdh.Collect.
    public double? Read()
    {
        if (Afterburner() is { } ab) { Source = "MSI Afterburner"; return ab; }
        if (AcpiZone() is { } z) { Source = "ACPI"; return z; }
        Source = "нет";
        return null;
    }

    /// MSI Afterburner: «CPU temperature» в общей памяти мониторинга.
    static double? Afterburner()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting("MAHMSharedMemory", MemoryMappedFileRights.Read);
            using var v = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (v.ReadUInt32(0) != 0x4D41484D) return null; // 'MAHM'; 0xDEAD — Afterburner закрыт
            var header = v.ReadUInt32(8);
            var count = v.ReadUInt32(12);
            var size = v.ReadUInt32(16);
            if (count > 512 || size < 1324) return null;
            double? best = null;
            for (var i = 0; i < count; i++)
            {
                long e = header + i * (long)size;
                var src = v.ReadUInt32(e + 1320);
                var name = ReadAnsi(v, e, 260);
                // 0x80 — MONITORING_SOURCE_ID_CPU_TEMPERATURE; «CPU1 temperature»… — по ядрам.
                if (src != 0x80 && !name.StartsWith("CPU temperature", StringComparison.OrdinalIgnoreCase)) continue;
                var t = v.ReadSingle(e + 1300);
                if (t is > 1 and < 130 && (best is null || t > best)) best = t;
            }
            return best;
        }
        catch (FileNotFoundException) { return null; }
        catch (Exception e)
        {
            Log.Write($"температура ЦП: Afterburner: {e.Message}");
            return null;
        }
    }

    static string ReadAnsi(MemoryMappedViewAccessor v, long at, int max)
    {
        var bytes = new byte[max];
        v.ReadArray(at, bytes, 0, max);
        var n = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.ASCII.GetString(bytes, 0, n < 0 ? max : n);
    }

    /// Зона ACPI: Кельвины. Многие платы держат там постоянное число (27,8 °C) — такое не берём.
    double? AcpiZone()
    {
        if (!_zone) return null;
        var values = _pdh.Values(Zone).Select(x => x.Value - 273.15).Where(t => t is > 5 and < 125).ToList();
        if (values.Count == 0) return null;
        var t = values.Max();
        _zoneMin = Math.Min(_zoneMin, t);
        _zoneMax = Math.Max(_zoneMax, t);
        // Живой датчик хоть раз меняется; пока не поменялся — не показываем.
        return _zoneMax - _zoneMin >= 1 ? t : null;
    }
}
