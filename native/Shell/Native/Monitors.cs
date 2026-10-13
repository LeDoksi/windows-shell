using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Native;

/// Монитор в физических пикселях.
public sealed record Screen(string Name, RECT Bounds, RECT Work, double Scale, bool Primary)
{
    /// Тот же монитор того же размера и масштаба (рабочую область не сравниваем: она меняется
    /// от наших же AppBar и от панели задач).
    public bool SameAs(Screen o) => Name == o.Name && Bounds.Left == o.Bounds.Left && Bounds.Top == o.Bounds.Top
        && Bounds.Right == o.Bounds.Right && Bounds.Bottom == o.Bounds.Bottom && Math.Abs(Scale - o.Scale) < 0.001 && Primary == o.Primary;

    public bool Contains(int x, int y) => x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom;
}

/// Подключённые мониторы. Список пересчитывается по сообщению Windows о смене мониторов и
/// раз в 2 секунды на всякий случай (сообщение о смене масштаба приходит не всегда).
public sealed class Monitors
{
    readonly DispatcherQueueTimer _poll;
    readonly DispatcherQueueTimer _debounce;

    public IReadOnlyList<Screen> All { get; private set; } = [];
    public Screen Primary => All.FirstOrDefault(s => s.Primary) ?? All[0];

    /// Набор мониторов, их размер или масштаб поменялись.
    public event Action? Changed;

    /// Поменялась только рабочая область (панель задач, AppBar).
    public event Action? WorkAreaChanged;

    public Monitors(DispatcherQueue queue, MessageWindow messages)
    {
        All = Read();
        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(250);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Refresh();
        _poll = queue.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        // Windows шлёт несколько сообщений подряд, пока перестраивает мониторы.
        messages.DisplayChanged += () => { _debounce.Stop(); _debounce.Start(); };
        messages.Resumed += () => { _debounce.Stop(); _debounce.Start(); };
    }

    public void Refresh()
    {
        var now = Read();
        if (now.Count == 0) return;
        var changed = now.Count != All.Count || now.Zip(All).Any(p => !p.First.SameAs(p.Second));
        var work = !changed && now.Zip(All).Any(p => !Same(p.First.Work, p.Second.Work));
        All = now;
        if (changed)
        {
            Log.Write("мониторы: " + string.Join("; ", now.Select(s => $"{s.Name} {s.Bounds.Width}×{s.Bounds.Height} {s.Scale * 100:0} %{(s.Primary ? " основной" : "")}")));
            Changed?.Invoke();
        }
        else if (work) WorkAreaChanged?.Invoke();
    }

    static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public Screen? ByName(string? name) => name is null ? null : All.FirstOrDefault(s => s.Name == name);

    /// Монитор под точкой (физические пиксели), иначе ближайший.
    public Screen At(int x, int y)
    {
        var hit = All.FirstOrDefault(s => s.Contains(x, y));
        if (hit != null) return hit;
        var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var name = DeviceName(mon);
        return All.FirstOrDefault(s => s.Name == name) ?? Primary;
    }

    /// Есть ли соседний монитор за точкой (курсор может «проскочить» через этот край).
    public bool HasNeighbour(int x, int y) => All.Any(s => s.Contains(x, y));

    static List<Screen> Read()
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) => { handles.Add(m); return true; }, IntPtr.Zero);
        var list = new List<Screen>();
        foreach (var m in handles)
        {
            var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (!GetMonitorInfoExW(m, ref info)) continue;
            list.Add(new Screen(info.szDevice, info.rcMonitor, info.rcWork, MonitorScale(m), (info.dwFlags & 1) != 0));
        }
        // Основной первым, остальные слева направо.
        return [.. list.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top)];
    }

    static string DeviceName(IntPtr mon)
    {
        var info = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
        return GetMonitorInfoExW(mon, ref info) ? info.szDevice : "";
    }

    delegate bool MonitorEnumProc(IntPtr mon, IntPtr dc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfoExW(IntPtr mon, ref MONITORINFOEXW info);
}
