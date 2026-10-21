using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WindowsShell.Config;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Clips;

/// История буфера обмена (design-v2 п. 5): запоминает текст, картинки и файлы, Win+V
/// открывает окошко у текстового курсора, выбранное вставляется в окно, где был курсор.
/// Выключено в настройках — ничего не запоминаем, Win+V снова у Windows.
public sealed class ClipsService
{
    const uint WM_CLIPBOARDUPDATE = 0x031D;

    readonly DispatcherQueue _queue;
    readonly ShellConfig _config;
    readonly MessageWindow _messages;
    readonly DispatcherQueueTimer _settle, _cleanup;
    readonly SemaphoreSlim _reading = new(1, 1);
    bool _listening, _loaded;
    uint _lastSeq;
    // Буфер, который положили мы сами при вставке: не новый пункт, а тот же самый.
    uint _ownSeq;
    ClipEntry? _ownEntry;

    public ClipStore Store { get; } = new();

    /// Список поменялся (окошко перестраивается).
    public event Action? Changed;

    public ClipsService(DispatcherQueue queue, ShellConfig config, MessageWindow messages)
    {
        _queue = queue;
        _config = config;
        _messages = messages;
        // Программы часто кладут форматы в несколько заходов: читаем, когда буфер успокоился.
        _settle = queue.CreateTimer();
        _settle.Interval = TimeSpan.FromMilliseconds(150);
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => Capture();
        _cleanup = queue.CreateTimer();
        _cleanup.Interval = TimeSpan.FromHours(1);
        _cleanup.Tick += (_, _) => { if (Store.Trim()) Changed?.Invoke(); };
        messages.Message += (msg, _, _) =>
        {
            if (msg == WM_CLIPBOARDUPDATE && _listening) { _settle.Stop(); _settle.Start(); }
        };
    }

    public bool Enabled => _config.WidgetEnabled("clipboard");

    /// Включить или выключить по настройкам (при запуске и при смене конфига).
    public void Apply()
    {
        if (Enabled && !_listening)
        {
            _listening = AddClipboardFormatListener(_messages.Hwnd);
            Log.Write(_listening ? "буфер обмена: история включена" : $"буфер обмена: не подписаться ({Marshal.GetLastWin32Error()})");
            _lastSeq = Board.Sequence;
            _cleanup.Start();
            if (!_loaded)
            {
                _loaded = true;
                var store = Store;
                Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var fresh = new ClipStore(store.Dir);
                    fresh.Load();
                    _queue.TryEnqueue(() =>
                    {
                        // Пока читали, могли что-то скопировать: оно сверху.
                        var added = store.History.ToList();
                        store.Pinned.AddRange(fresh.Pinned);
                        foreach (var e in fresh.History) if (added.All(a => a.Hash != e.Hash)) store.History.Add(e);
                        store.Trim();
                        store.Save();
                        Log.Write($"буфер обмена: история {store.History.Count}, закреплённых {store.Pinned.Count} ({sw.ElapsedMilliseconds} мс)");
                        Changed?.Invoke();
                    });
                });
            }
        }
        else if (!Enabled && _listening)
        {
            RemoveClipboardFormatListener(_messages.Hwnd);
            _listening = false;
            _cleanup.Stop();
            Log.Write("буфер обмена: история выключена");
        }
    }

    void Capture()
    {
        var seq = Board.Sequence;
        if (seq == _lastSeq) return;
        _lastSeq = seq;
        if (seq == _ownSeq)
        {
            if (_ownEntry != null) { Store.Touch(_ownEntry); Changed?.Invoke(); }
            return;
        }
        Task.Run(async () =>
        {
            await _reading.WaitAsync();
            try
            {
                var snap = Board.Read(test: false, out var skip);
                if (snap is null)
                {
                    if (skip != null && !skip.StartsWith("нет текста")) Log.Write($"буфер обмена: не запоминаем — {skip}");
                    return;
                }
                var entry = Store.Prepare(snap);
                if (entry is null) return;
                _queue.TryEnqueue(() =>
                {
                    if (!_listening) { Store.Discard(entry); return; }
                    Store.Commit(entry);
                    Changed?.Invoke();
                });
            }
            catch (Exception e)
            {
                Log.Error("буфер обмена: чтение", e);
            }
            finally
            {
                _reading.Release();
            }
        });
    }

    // ---------- действия ----------

    /// Положить пункт в буфер. `plain` — только текст, без оформления.
    public bool PutBack(ClipEntry e, bool plain)
    {
        bool ok;
        try
        {
            ok = e.Kind switch
            {
                ClipKind.Text => Board.PutText(Store.ReadText(e),
                    plain || !e.HasHtml ? null : Store.ReadBytes(e, ".html"),
                    plain || !e.HasRtf ? null : Store.ReadBytes(e, ".rtf")),
                ClipKind.Image => Store.ReadBytes(e, ".png") is { } png && Board.PutImage(png),
                _ => e.Files is { Count: > 0 } files && Board.PutFiles(files),
            };
        }
        catch (Exception ex)
        {
            Log.Error("буфер обмена: положить обратно", ex);
            ok = false;
        }
        if (ok)
        {
            _ownSeq = Board.Sequence;
            _ownEntry = e;
        }
        return ok;
    }

    /// Вставить пункт в окно `target` (окошко уже закрыто и фокус отдан окну). Окно не наше
    /// или пропало — пункт просто остаётся в буфере.
    public async void Paste(ClipEntry e, bool plain, IntPtr target)
    {
        if (!PutBack(e, plain))
        {
            Toasts.Show("Не получилось вставить", "Буфер обмена занят другой программой. Попробуй ещё раз.", silent: true);
            return;
        }
        if (target == IntPtr.Zero || !IsWindow(target)) return;
        // Ждём, пока окно правда станет активным (до полсекунды).
        for (var i = 0; i < 25 && !IsForeground(target); i++) await Task.Delay(20);
        if (!IsForeground(target))
        {
            Log.Write("буфер обмена: окно для вставки не стало активным — оставили в буфере");
            return;
        }
        if (Elevated(target) && !Elevated(IntPtr.Zero))
        {
            // Окну администратора Windows не даёт нажимать клавиши от обычной программы.
            Toasts.Show("Скопировано", "Окно запущено от администратора: вставь сам, Ctrl+V.", silent: true);
            return;
        }
        await Task.Delay(30);
        WinKeys.SendPaste();
        Log.Write($"буфер обмена: вставлено ({e.Kind}{(plain ? ", без оформления" : "")})");
    }

    static bool IsForeground(IntPtr target)
    {
        var fg = GetForegroundWindow();
        return fg == target || GetAncestor(fg, GA_ROOTOWNER) == GetAncestor(target, GA_ROOTOWNER);
    }

    public void SetPinned(ClipEntry e, bool pinned) { Store.SetPinned(e, pinned); Changed?.Invoke(); }
    public void MovePinned(ClipEntry e, int index) { Store.MovePinned(e, index); Changed?.Invoke(); }
    public void Remove(ClipEntry e) { Store.Remove(e); Changed?.Invoke(); }
    public void ClearHistory() { Store.ClearHistory(); Changed?.Invoke(); }

    /// Окно запущено от администратора (IntPtr.Zero — мы сами).
    static bool Elevated(IntPtr hwnd)
    {
        IntPtr process;
        var own = hwnd == IntPtr.Zero;
        if (own) process = GetCurrentProcess();
        else
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            // Не открыть даже на чтение сведений — значит, выше нас.
            if (process == IntPtr.Zero) return true;
        }
        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return false;
            try { return GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, 4, out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally
        {
            if (!own) CloseHandle(process);
        }
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int size, out int ret);
}
