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
        if (Afterburner.Temperature(0x80, "CPU") is { } ab) { Source = "MSI Afterburner"; return ab; }
        if (AcpiZone() is { } z) { Source = "ACPI"; return z; }
        Source = "нет";
        return null;
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
