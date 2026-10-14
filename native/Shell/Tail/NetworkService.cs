using Microsoft.UI.Dispatching;
using Windows.Networking.Connectivity;

namespace WindowsShell.Tail;

public enum NetKind { None, Ethernet, Wifi, Cellular }

/// Что показывает значок сети: кабель, Wi-Fi с уровнем, нет сети.
public sealed record NetState(NetKind Kind, string Name, bool Internet, int Bars)
{
    public string Glyph => Kind switch
    {
        NetKind.Ethernet => Internet ? "\uE839" : "\uEB56",
        NetKind.Wifi when !Internet => "",
        NetKind.Wifi => Bars switch { <= 1 => "", 2 => "", 3 => "", _ => "" },
        NetKind.Cellular => "",
        _ => "",
    };

    public string Status => Kind switch
    {
        NetKind.None => "Нет подключения к интернету",
        _ when !Internet => "Подключено, без интернета",
        _ => "Подключено, есть интернет",
    };
}

/// Состояние сети (WSH-48) от Windows: меняется — значок обновляется сразу.
public sealed class NetworkService
{
    readonly DispatcherQueue _queue;
    readonly DispatcherQueueTimer _debounce;
    public NetState State { get; private set; } = new(NetKind.None, "", false, 0);
    public event Action? Changed;

    public NetworkService(DispatcherQueue queue)
    {
        _queue = queue;
        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(300);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Refresh();
        try
        {
            // Событие приходит из чужого потока и пачкой: пересчёт один, в потоке интерфейса.
            NetworkInformation.NetworkStatusChanged += _ => _queue.TryEnqueue(() => { _debounce.Stop(); _debounce.Start(); });
        }
        catch (Exception e) { Log.Error("сеть: подписка", e); }
        Refresh();
    }

    public void Refresh()
    {
        NetState next;
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p is null) next = new(NetKind.None, "", false, 0);
            else
            {
                var level = p.GetNetworkConnectivityLevel();
                var internet = level == NetworkConnectivityLevel.InternetAccess;
                var kind = p.IsWlanConnectionProfile ? NetKind.Wifi : p.IsWwanConnectionProfile ? NetKind.Cellular : NetKind.Ethernet;
                var name = kind == NetKind.Wifi ? p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? p.ProfileName : p.ProfileName;
                var bars = p.GetSignalBars() is byte b ? b : 5;
                next = new(kind, name ?? "", internet, bars);
            }
        }
        catch (Exception e)
        {
            Log.Error("сеть", e);
            return;
        }
        if (next == State) return;
        State = next;
        Changed?.Invoke();
    }
}
