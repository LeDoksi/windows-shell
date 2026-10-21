using Windows.Devices.Bluetooth.Advertisement;

namespace WindowsShell.Charge;

/// Заряд AirPods и Beats: Windows его не знает, но наушники Apple сами всё время рассказывают
/// о нём в Bluetooth-объявлении (данные Apple 0x004C, тип 0x07: левый, правый, футляр,
/// по 10 %). Так делают OpenPods и AirPodsDesktop. Слушаем, только пока такие наушники
/// подключены; соседские отсекаем по силе сигнала (свои рядом — самые громкие).
public static class AirPods
{
    public sealed record Snapshot(int? Left, int? Right, int? Case, bool LeftCharging, bool RightCharging, bool CaseCharging, short Rssi, DateTime At)
    {
        /// Уровень наушников: меньший из тех, что известны (наушник в футляре с закрытой крышкой не виден).
        public int? Percent => Left is { } l && Right is { } r ? Math.Min(l, r) : Left ?? Right;

        public bool Charging => (Left is null || LeftCharging) && (Right is null || RightCharging) && Percent != null;

        public string Detail
        {
            get
            {
                var parts = new List<string>();
                if (Left is { } l) parts.Add($"Л {l} %{(LeftCharging ? "⚡" : "")}");
                if (Right is { } r) parts.Add($"П {r} %{(RightCharging ? "⚡" : "")}");
                if (Case is { } c) parts.Add($"футляр {c} %{(CaseCharging ? "⚡" : "")}");
                return string.Join(" · ", parts);
            }
        }
    }

    const ushort Apple = 0x004C;
    static readonly object Lock = new();
    static BluetoothLEAdvertisementWatcher? _watcher;
    static Snapshot? _last;

    /// Уровни поменялись (фоновый поток).
    public static event Action? Changed;

    /// Свежие данные (не старше 2 минут) или null.
    public static Snapshot? Latest
    {
        get
        {
            lock (Lock) return _last is { } s && DateTime.UtcNow - s.At < TimeSpan.FromMinutes(2) ? s : null;
        }
    }

    public static bool IsApple(string name) =>
        name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || name.Contains("Beats", StringComparison.OrdinalIgnoreCase);

    /// Слушать объявления, пока подключены наушники Apple.
    public static void Listen(bool on)
    {
        lock (Lock)
        {
            if (on == (_watcher != null)) return;
            if (!on)
            {
                try { _watcher!.Stop(); } catch { }
                _watcher = null;
                _last = null;
                return;
            }
            try
            {
                var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                w.Received += OnReceived;
                w.Start();
                _watcher = w;
                Log.Write("заряд: слушаю AirPods");
            }
            catch (Exception e)
            {
                Log.Error("заряд: AirPods", e);
            }
        }
    }

    static void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        foreach (var m in args.Advertisement.ManufacturerData)
        {
            if (m.CompanyId != Apple || m.Data.Length < 9) continue;
            var d = new byte[m.Data.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(m.Data)) reader.ReadBytes(d);
            if (Parse(d, args.RawSignalStrengthInDBm) is not { } s) continue;
            bool changed;
            lock (Lock)
            {
                var old = _last;
                // Сосед за стенкой тоже вещает: берём самый громкий сигнал за последние 15 секунд.
                if (s.Rssi < -75 || old != null && DateTime.UtcNow - old.At < TimeSpan.FromSeconds(15) && s.Rssi < old.Rssi - 10) continue;
                _last = s;
                changed = old is null || old.Left != s.Left || old.Right != s.Right || old.Case != s.Case || old.Charging != s.Charging;
            }
            if (changed) Changed?.Invoke();
        }
    }

    /// Разбор «proximity pairing» (тип 0x07, длина 0x19): байт 5 — состояние (бит «перевёрнуты»),
    /// байт 6 — два наушника по полубайту, байт 7 — флаги зарядки и футляр. 0…10 — десятки процентов, 15 — нет данных.
    public static Snapshot? Parse(byte[] d, short rssi)
    {
        if (d.Length < 9 || d[0] != 0x07 || d[1] != 0x19) return null;
        var flip = (d[5] >> 4 & 0x02) == 0;
        int hi6 = d[6] >> 4, lo6 = d[6] & 0x0F, charge = d[7] >> 4, box = d[7] & 0x0F;
        static int? Level(int v) => v <= 10 ? v * 10 : null;
        var left = Level(flip ? hi6 : lo6);
        var right = Level(flip ? lo6 : hi6);
        var caseLevel = Level(box);
        return new Snapshot(left, right, caseLevel,
            (charge & (flip ? 0b10 : 0b01)) != 0, (charge & (flip ? 0b01 : 0b10)) != 0, (charge & 0b100) != 0, rssi, DateTime.UtcNow);
    }
}
