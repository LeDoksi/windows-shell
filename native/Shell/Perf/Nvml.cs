using System.Runtime.InteropServices;

namespace WindowsShell.Perf;

/// NVML — библиотека из драйвера NVIDIA (System32\nvml.dll): температура видеокарты без прав
/// администратора. Нет драйвера NVIDIA — просто null.
internal static class Nvml
{
    static bool _tried, _ok;
    static IntPtr _device;
    static Init? _init;
    static GetTemp? _temp;

    public static double? Temperature(uint luidLow, int luidHigh)
    {
        if (!_tried)
        {
            _tried = true;
            try { _ok = Load(); }
            catch (Exception e) { Log.Write($"NVML: {e.Message}"); }
            Log.Write($"NVML: {(_ok ? "есть" : "нет")}");
        }
        if (!_ok || _temp is null) return null;
        return _temp(_device, 0 /* NVML_TEMPERATURE_GPU */, out var t) == 0 && t is > 0 and < 150 ? t : null;
    }

    static bool Load()
    {
        var path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!NativeLibrary.TryLoad(path, out var lib) && !NativeLibrary.TryLoad("nvml.dll", out lib)) return false;
        _init = Marshal.GetDelegateForFunctionPointer<Init>(NativeLibrary.GetExport(lib, "nvmlInit_v2"));
        var count = Marshal.GetDelegateForFunctionPointer<GetCount>(NativeLibrary.GetExport(lib, "nvmlDeviceGetCount_v2"));
        var byIndex = Marshal.GetDelegateForFunctionPointer<ByIndex>(NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2"));
        _temp = Marshal.GetDelegateForFunctionPointer<GetTemp>(NativeLibrary.GetExport(lib, "nvmlDeviceGetTemperature"));
        if (_init() != 0 || count(out var n) != 0 || n == 0) return false;
        // Обычно одна видеокарта NVIDIA; берём первую.
        return byIndex(0, out _device) == 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ByIndex(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetTemp(IntPtr device, int sensor, out uint temp);
}
