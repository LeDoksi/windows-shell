using System.Runtime.InteropServices;
using WindowsShell.Audio;

namespace WindowsShell.Shots;

/// Звук для записи экрана: звук системы (то, что слышно в колонках — WASAPI loopback) и
/// микрофон, смешанные в 48 кГц стерео. Оба включаются и выключаются прямо во время записи.
/// Захват идёт в своём потоке (MTA); запись забирает готовые кусочки из очереди.
internal sealed class AudioMix : IDisposable
{
    public const int Rate = 48000;
    const int MaxBuffered = Rate / 4; // не больше 250 мс в очереди: остальное старое, выбрасываем

    readonly Thread _thread;
    readonly Fifo _system = new(), _mic = new();
    volatile bool _stop;

    public volatile bool SystemOn, MicOn, Paused;

    public AudioMix(bool system, bool mic)
    {
        SystemOn = system;
        MicOn = mic;
        _thread = new Thread(Run) { IsBackground = true, Name = "запись: звук" };
        _thread.Start();
    }

    /// Начало записи: всё, что накопилось до неё, не нужно.
    public void Reset()
    {
        _system.Clear();
        _mic.Clear();
    }

    /// Взять `frames` кадров смеси (стерео, 16 бит). Чего нет — тишина.
    public byte[] Read(int frames)
    {
        var a = new float[frames * 2];
        var b = new float[frames * 2];
        _system.Take(a);
        _mic.Take(b);
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames * 2; i++)
        {
            var v = Math.Clamp(a[i] + b[i], -1f, 1f);
            var s = (short)(v * 32767);
            bytes[i * 2] = (byte)s;
            bytes[i * 2 + 1] = (byte)(s >> 8);
        }
        return bytes;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
    }

    void Run()
    {
        Source? system = null, mic = null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            system = Source.Open(enumerator, EDataFlow.Render, loopback: true);
            var micTried = false;
            while (!_stop)
            {
                // Микрофон открываем, только когда его включили (Windows покажет значок «микрофон занят»).
                if (MicOn && mic is null && !micTried)
                {
                    micTried = true;
                    mic = Source.Open(enumerator, EDataFlow.Capture, loopback: false);
                }
                system?.Drain(SystemOn && !Paused ? _system : null);
                mic?.Drain(MicOn && !Paused ? _mic : null);
                Thread.Sleep(10);
            }
        }
        catch (Exception e)
        {
            Log.Error("запись: звук", e);
        }
        finally
        {
            system?.Dispose();
            mic?.Dispose();
        }
    }

    /// Очередь отсчётов стерео (float), не длиннее 250 мс.
    sealed class Fifo
    {
        readonly Queue<float> _q = new();

        public void Put(ReadOnlySpan<float> stereo)
        {
            lock (_q)
            {
                foreach (var v in stereo) _q.Enqueue(v);
                while (_q.Count > MaxBuffered * 2) _q.Dequeue();
            }
        }

        public void Take(Span<float> dst)
        {
            lock (_q)
            {
                var n = Math.Min(dst.Length, _q.Count);
                for (var i = 0; i < n; i++) dst[i] = _q.Dequeue();
            }
        }

        public void Clear()
        {
            lock (_q) _q.Clear();
        }
    }

    /// Устройство Windows (колонки в режиме «что слышно» или микрофон) и перевод его звука в
    /// 48 кГц стерео float.
    sealed class Source : IDisposable
    {
        readonly IAudioClient _client;
        readonly IAudioCaptureClient _capture;
        readonly int _rate, _channels;
        readonly bool _float;
        readonly string _name;
        double _pos; // дробная позиция пересчёта частоты
        float _lastL, _lastR;

        Source(IAudioClient client, IAudioCaptureClient capture, int rate, int channels, bool isFloat, string name)
        {
            _client = client;
            _capture = capture;
            _rate = rate;
            _channels = channels;
            _float = isFloat;
            _name = name;
        }

        public static Source? Open(IMMDeviceEnumerator enumerator, EDataFlow flow, bool loopback)
        {
            var name = loopback ? "звук системы" : "микрофон";
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var device) != 0 || device is null)
                {
                    Log.Write($"запись: {name} — устройства нет");
                    return null;
                }
                var iid = typeof(IAudioClient).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) != 0) return null;
                var client = (IAudioClient)o;
                var flags = loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0u;
                // Просим Windows сразу отдавать 48 кГц стерео float; не согласилась — берём как есть.
                var want = new WAVEFORMATEX { wFormatTag = 3, nChannels = 2, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * 8, nBlockAlign = 8, wBitsPerSample = 32 };
                int rate = Rate, channels = 2;
                var isFloat = true;
                var wantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
                Marshal.StructureToPtr(want, wantPtr, false);
                var hr = client.Initialize(0, flags | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 2_000_000, 0, wantPtr, IntPtr.Zero);
                Marshal.FreeHGlobal(wantPtr);
                if (hr != 0)
                {
                    Marshal.ReleaseComObject(client);
                    if (device.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return null;
                    client = (IAudioClient)o;
                    if (client.GetMixFormat(out var mixPtr) != 0) return null;
                    var mix = Marshal.PtrToStructure<WAVEFORMATEX>(mixPtr);
                    // WAVE_FORMAT_EXTENSIBLE: подтип лежит дальше; у общего формата Windows это почти всегда float.
                    isFloat = mix.wFormatTag == 3 || (mix.wFormatTag == 0xFFFE && mix.wBitsPerSample == 32);
                    rate = (int)mix.nSamplesPerSec;
                    channels = mix.nChannels;
                    hr = client.Initialize(0, flags, 2_000_000, 0, mixPtr, IntPtr.Zero);
                    Marshal.FreeCoTaskMem(mixPtr);
                    if (hr != 0) { Log.Write($"запись: {name} не открылся (0x{hr:X8})"); return null; }
                    if (!isFloat && mix.wBitsPerSample != 16) { Log.Write($"запись: {name} — формат {mix.wBitsPerSample} бит не поддержан"); return null; }
                }
                var iidCapture = typeof(IAudioCaptureClient).GUID;
                if (client.GetService(ref iidCapture, out var c) != 0) return null;
                var capture = (IAudioCaptureClient)c;
                client.Start();
                Log.Write($"запись: {name} {rate} Гц, каналов {channels}{(isFloat ? "" : ", 16 бит")}");
                return new Source(client, capture, rate, channels, isFloat, name);
            }
            catch (Exception e)
            {
                Log.Error($"запись: {name}", e);
                return null;
            }
        }

        /// Забрать всё, что пришло; `into` null — выбросить (источник выключен или пауза).
        public unsafe void Drain(Fifo? into)
        {
            while (_capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
            {
                if (_capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) != 0) return;
                try
                {
                    if (into is null || frames == 0) continue;
                    var stereo = new float[frames * 2];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                    {
                        for (var i = 0; i < frames; i++)
                        {
                            float l, r;
                            if (_float)
                            {
                                var p = (float*)data + i * _channels;
                                l = p[0];
                                r = _channels > 1 ? p[1] : l;
                            }
                            else
                            {
                                var p = (short*)data + i * _channels;
                                l = p[0] / 32768f;
                                r = _channels > 1 ? p[1] / 32768f : l;
                            }
                            stereo[i * 2] = l;
                            stereo[i * 2 + 1] = r;
                        }
                    }
                    into.Put(_rate == Rate ? stereo : Resample(stereo));
                }
                finally
                {
                    _capture.ReleaseBuffer(frames);
                }
            }
        }

        /// Линейный пересчёт в 48 кГц (если устройство работает на 44,1 кГц и т. п.).
        float[] Resample(float[] input)
        {
            var frames = input.Length / 2;
            var step = (double)_rate / Rate;
            var output = new List<float>((int)(frames / step) + 4);
            while (_pos < frames)
            {
                var i = (int)_pos;
                var t = (float)(_pos - i);
                float l0 = i == 0 ? _lastL : input[(i - 1) * 2], r0 = i == 0 ? _lastR : input[(i - 1) * 2 + 1];
                float l1 = input[i * 2], r1 = input[i * 2 + 1];
                output.Add(l0 + (l1 - l0) * t);
                output.Add(r0 + (r1 - r0) * t);
                _pos += step;
            }
            _pos -= frames;
            if (frames > 0) { _lastL = input[(frames - 1) * 2]; _lastR = input[(frames - 1) * 2 + 1]; }
            return [.. output];
        }

        public void Dispose()
        {
            try { _client.Stop(); } catch { }
            Marshal.ReleaseComObject(_capture);
            Marshal.ReleaseComObject(_client);
            Log.Write($"запись: {_name} закрыт");
        }
    }

    const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000, AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }
}
