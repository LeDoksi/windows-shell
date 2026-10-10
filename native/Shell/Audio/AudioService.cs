using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WindowsShell.Native;

namespace WindowsShell.Audio;

public sealed record AudioDevice(string Id, string Name);

internal sealed class AppSession
{
    public required uint Pid { get; init; }
    public required string Name { get; init; }
    public string? Exe { get; init; }
    public bool System { get; init; }
    internal required ISimpleAudioVolume Volume { get; init; }

    public float Level
    {
        get { Volume.GetMasterVolume(out var v); return v; }
        set { var g = AudioService.Context; Volume.SetMasterVolume(Math.Clamp(value, 0, 1), ref g); }
    }

    public bool Muted
    {
        get { Volume.GetMute(out var m); return m; }
        set { var g = AudioService.Context; Volume.SetMute(value, ref g); }
    }
}

/// Звук через Core Audio (как `audio.rs`): громкость и mute устройства по умолчанию, список
/// устройств, смена устройства по умолчанию, громкость приложений. Изменения снаружи
/// (медиаклавиши, микшер Windows) приходят событием `Changed` в потоке интерфейса.
public sealed class AudioService
{
    /// Наш контекст изменений: чтобы отличать свои изменения от чужих.
    internal static Guid Context = new("6B1B4B8C-0E6F-4C0B-9C51-5E8C1D2A7F31");

    readonly DispatcherQueue _queue;
    readonly IMMDeviceEnumerator _enumerator;
    readonly Notifications _notifications;
    readonly VolumeCallback _callback;
    IAudioEndpointVolume? _output;
    IAudioEndpointVolume? _input;

    public event Action? Changed;

    public AudioService(DispatcherQueue queue)
    {
        _queue = queue;
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        _callback = new VolumeCallback(this);
        _notifications = new Notifications(this);
        _enumerator.RegisterEndpointNotificationCallback(_notifications);
        Rebind();
    }

    void Notify() => _queue.TryEnqueue(() => Changed?.Invoke());

    /// Устройство по умолчанию сменилось — подписываемся на новое.
    public void Rebind()
    {
        if (_output != null) { try { _output.UnregisterControlChangeNotify(_callback); } catch { } }
        _output = Endpoint(EDataFlow.Render);
        _output?.RegisterControlChangeNotify(_callback);
        if (_input != null) { try { _input.UnregisterControlChangeNotify(_callback); } catch { } }
        _input = Endpoint(EDataFlow.Capture);
        _input?.RegisterControlChangeNotify(_callback);
    }

    IMMDevice? DefaultDevice(EDataFlow flow) =>
        _enumerator.GetDefaultAudioEndpoint(flow, ERole.Multimedia, out var d) == 0 ? d : null;

    IAudioEndpointVolume? Endpoint(EDataFlow flow)
    {
        var d = DefaultDevice(flow);
        if (d is null) return null;
        var iid = typeof(IAudioEndpointVolume).GUID;
        return d.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) == 0 ? (IAudioEndpointVolume)o : null;
    }

    public bool HasOutput => _output != null;
    public bool HasInput => _input != null;

    public int Volume
    {
        get => _output != null && _output.GetMasterVolumeLevelScalar(out var v) == 0 ? (int)Math.Round(v * 100) : 0;
        set { if (_output != null) { var g = Context; _output.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 100) / 100f, ref g); } }
    }

    public bool Muted
    {
        get => _output != null && _output.GetMute(out var m) == 0 && m;
        set { if (_output != null) { var g = Context; _output.SetMute(value, ref g); } }
    }

    public int MicVolume
    {
        get => _input != null && _input.GetMasterVolumeLevelScalar(out var v) == 0 ? (int)Math.Round(v * 100) : 0;
        set { if (_input != null) { var g = Context; _input.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 100) / 100f, ref g); } }
    }

    public bool MicMuted
    {
        get => _input != null && _input.GetMute(out var m) == 0 && m;
        set { if (_input != null) { var g = Context; _input.SetMute(value, ref g); } }
    }

    public string? DefaultId(EDataFlow flow = EDataFlow.Render) =>
        DefaultDevice(flow) is { } d && d.GetId(out var id) == 0 ? id : null;

    static readonly Win32.PROPERTYKEY FriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);

    public List<AudioDevice> Devices(EDataFlow flow = EDataFlow.Render)
    {
        var list = new List<AudioDevice>();
        if (_enumerator.EnumAudioEndpoints(flow, 1 /* ACTIVE */, out var all) != 0) return list;
        all.GetCount(out var n);
        for (uint i = 0; i < n; i++)
        {
            if (all.Item(i, out var d) != 0 || d.GetId(out var id) != 0) continue;
            var name = id;
            if (d.OpenPropertyStore(0, out var store) == 0)
            {
                var key = FriendlyName;
                if (store.GetValue(ref key, out var pv) == 0)
                {
                    if (pv.vt == Win32.VT_LPWSTR && pv.p != IntPtr.Zero) name = Marshal.PtrToStringUni(pv.p) ?? id;
                    Win32.PropVariantClear(ref pv);
                }
                Marshal.ReleaseComObject(store);
            }
            list.Add(new AudioDevice(id, name));
        }
        return list;
    }

    /// Делает устройство основным для всех ролей, как это делает Windows.
    public bool SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            var ok = true;
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                ok &= policy.SetDefaultEndpoint(id, role) == 0;
            return ok;
        }
        catch { return false; }
    }

    /// Следующее устройство вывода по кругу (горячая клавиша).
    public void NextOutput()
    {
        var all = Devices();
        if (all.Count < 2) return;
        var i = all.FindIndex(d => d.Id == DefaultId());
        SetDefault(all[(i + 1) % all.Count].Id);
    }

    /// Приложения, которые сейчас могут играть звук на устройстве по умолчанию.
    internal List<AppSession> Sessions()
    {
        var list = new List<AppSession>();
        var d = DefaultDevice(EDataFlow.Render);
        if (d is null) return list;
        var iid = typeof(IAudioSessionManager2).GUID;
        if (d.Activate(ref iid, 23, IntPtr.Zero, out var o) != 0) return list;
        var manager = (IAudioSessionManager2)o;
        if (manager.GetSessionEnumerator(out var sessions) != 0) return list;
        sessions.GetCount(out var count);
        var seen = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out var s) != 0) continue;
            if (s.GetState(out var state) != 0 || state == 2 /* expired */) continue;
            s.GetProcessId(out var pid);
            var system = s.IsSystemSoundsSession() == 0;
            if (!system && (pid == 0 || !seen.Add(pid))) continue;
            string? exe = null;
            string name;
            if (system) name = "Системные звуки";
            else
            {
                exe = Win32.ProcessInfo(pid).Exe;
                string? description = null;
                if (exe != null) { try { description = FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { } }
                s.GetDisplayName(out var display);
                name = new[] { description, display, exe != null ? Path.GetFileNameWithoutExtension(exe) : null }
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith('@')) ?? $"Процесс {pid}";
            }
            list.Add(new AppSession { Pid = pid, Name = name.Trim(), Exe = exe, System = system, Volume = (ISimpleAudioVolume)s });
        }
        // Системные звуки — последними, как в микшере Windows.
        return [.. list.OrderBy(x => x.System).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    [ComVisible(true)]
    sealed class VolumeCallback(AudioService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data) { owner.Notify(); return 0; }
    }

    [ComVisible(true)]
    sealed class Notifications(AudioService owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
        {
            if (role != ERole.Multimedia) return;
            owner._queue.TryEnqueue(() => { owner.Rebind(); owner.Changed?.Invoke(); });
        }
        public void OnDeviceAdded(string id) => owner.Notify();
        public void OnDeviceRemoved(string id) => owner.Notify();
        public void OnDeviceStateChanged(string id, int state) => owner.Notify();
        public void OnPropertyValueChanged(string id, Win32.PROPERTYKEY key) { }
    }
}
