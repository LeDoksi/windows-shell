using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using static WindowsShell.Native.Win32;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using D = System.Drawing;

namespace WindowsShell.Shots;

/// Проверка перетаскивания снимка без мыши (для проверки через удалённый доступ):
/// `WindowsShellNative.exe --drag-test`. Ставит посреди основного монитора своё окно-приёмник
/// (как чат или папка), наводит на него курсор и тащит туда снимок через FileDrag: бросить,
/// отменить, «кнопка уже отпущена», второй раз поверх первого. Окно висит ~3 секунды, курсор
/// потом возвращается на место. Итог — в `drag-test.txt` рядом с exe и в журнале.
public static class DragTest
{
    public const string Arg = "--drag-test";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        var failed = 0;
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проверка перетаскивания: {line}");
        }
        void Check(bool ok, string what)
        {
            if (!ok) failed++;
            Say($"{(ok ? "ок  " : "НЕТ ")} {what}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        GetCursorPos(out var cursor);
        Target? target = null;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "WindowsShell-drag-test.png");
            using (var bmp = new D.Bitmap(320, 200))
            {
                using (var g = D.Graphics.FromImage(bmp)) g.Clear(D.Color.SteelBlue);
                bmp.Save(path, ImageFormat.Png);
            }
            Say($"файл: {path}");

            var bounds = MonitorInfo(MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY)).rcMonitor;
            target = Target.Open(bounds.Left + bounds.Width / 2 - 150, bounds.Top + bounds.Height / 2 - 150, 300);
            SetCursorPos(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Thread.Sleep(200);

            // 1. Держим «кнопку» секунду и бросаем: приёмник получает файл.
            var r = Drag(path, after: 1000, FileDrag.DRAGDROP_S_DROP, target);
            Check(r.Ended && r.Dropped, $"бросили: перетаскивание закончилось за {r.Ms} мс, файл принят = {r.Dropped}");
            Check(target.Enter >= 1 && target.Over >= 3, $"над приёмником: вошли {target.Enter}, двигались {target.Over} раз (цикл идёт и без движения мыши)");
            Check(string.Equals(target.DroppedPath, path, StringComparison.OrdinalIgnoreCase), $"приёмник получил «{target.DroppedPath}»");
            Check(r.Ms >= 900 && r.Ms < 4000, "не оборвалось раньше времени и не зависло");

            // 2. Esc: отмена, приёмнику — «ушли».
            r = Drag(path, after: 500, FileDrag.DRAGDROP_S_CANCEL, target);
            Check(r.Ended && !r.Dropped && target.Leave >= 1 && target.DroppedPath is null, $"отмена: закончилось за {r.Ms} мс, бросили = {r.Dropped}, ушли {target.Leave}");

            // 3. Настоящая проверка кнопки: она не зажата — сразу бросает туда, где курсор.
            target.Reset();
            var done = new ManualResetEventSlim();
            var dropped = false;
            var sw = Stopwatch.StartNew();
            var started = FileDrag.Start(IntPtr.Zero, path, d => { dropped = d; done.Set(); });
            // 4. Пока тащим, второе перетаскивание не начинается.
            var second = FileDrag.Start(IntPtr.Zero, path, _ => { });
            var ended = done.Wait(5000);
            Check(started && ended && dropped, $"кнопка мыши отпущена: бросили за {sw.ElapsedMilliseconds} мс, файл принят = {dropped}");
            Check(!second, "второе перетаскивание поверх первого не началось");
        }
        catch (Exception e)
        {
            failed++;
            Say($"ОШИБКА: {e}");
        }
        finally
        {
            target?.Close();
            SetCursorPos(cursor.X, cursor.Y);
        }
        Say(failed == 0 ? "ГОТОВО: всё в порядке" : $"ГОТОВО: не прошло {failed}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "drag-test.txt"), report.ToString());
        return true;
    }

    record struct Result(bool Ended, bool Dropped, long Ms);

    static Result Drag(string path, int after, int verdict, Target target)
    {
        target.Reset();
        var done = new ManualResetEventSlim();
        var dropped = false;
        var sw = Stopwatch.StartNew();
        FileDrag.Start(IntPtr.Zero, path, d => { dropped = d; done.Set(); }, () => sw.ElapsedMilliseconds < after ? 0 : verdict);
        var ended = done.Wait(10_000);
        return new Result(ended, dropped, sw.ElapsedMilliseconds);
    }

    /// Окно-приёмник в своём потоке, как у любой программы, куда бросают файлы.
    [ComVisible(true)]
    sealed class Target : IDropTarget
    {
        volatile bool _stop;
        IntPtr _hwnd;
        Thread? _thread;
        public volatile int Enter, Over, Leave;
        public volatile string? DroppedPath;

        public static Target Open(int x, int y, int size)
        {
            var t = new Target();
            var ready = new ManualResetEventSlim();
            t._thread = new Thread(() =>
            {
                OleInitialize(IntPtr.Zero);
                // STATIC с SS_NOTIFY: без него окно «прозрачно» для WindowFromPoint и OLE его не найдёт.
                t._hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "STATIC", "WindowsShell drag test", WS_POPUP | WS_VISIBLE | SS_NOTIFY, x, y, size, size, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                var hr = RegisterDragDrop(t._hwnd, t);
                if (hr != 0) Log.Write($"проверка перетаскивания: RegisterDragDrop 0x{hr:X}");
                ready.Set();
                while (!t._stop)
                {
                    while (PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
                    {
                        TranslateMessage(ref m);
                        DispatchMessageW(ref m);
                    }
                    Thread.Sleep(5);
                }
                RevokeDragDrop(t._hwnd);
                DestroyWindow(t._hwnd);
                OleUninitialize();
            }) { IsBackground = true };
            t._thread.SetApartmentState(ApartmentState.STA);
            t._thread.Start();
            ready.Wait(5000);
            return t;
        }

        public void Reset()
        {
            Enter = Over = Leave = 0;
            DroppedPath = null;
        }

        public void Close()
        {
            _stop = true;
            _thread?.Join(3000);
        }

        public int DragEnter(ComTypes.IDataObject data, int keys, POINT pt, ref int effect)
        {
            Enter++;
            effect &= DROPEFFECT_COPY;
            return 0;
        }

        public int DragOver(int keys, POINT pt, ref int effect)
        {
            Over++;
            effect &= DROPEFFECT_COPY;
            return 0;
        }

        public int DragLeave()
        {
            Leave++;
            return 0;
        }

        public int Drop(ComTypes.IDataObject data, int keys, POINT pt, ref int effect)
        {
            effect &= DROPEFFECT_COPY;
            var format = new ComTypes.FORMATETC { cfFormat = CF_HDROP, dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = ComTypes.TYMED.TYMED_HGLOBAL };
            try
            {
                data.GetData(ref format, out var medium);
                var name = new StringBuilder(1024);
                if (DragQueryFileW(medium.unionmember, 0, name, name.Capacity) > 0) DroppedPath = name.ToString();
                ReleaseStgMedium(ref medium);
            }
            catch (Exception e) { Log.Write($"проверка перетаскивания: CF_HDROP не прочитан ({e.Message})"); }
            return 0;
        }
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject data, int keys, POINT pt, ref int effect);
        [PreserveSig] int DragOver(int keys, POINT pt, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject data, int keys, POINT pt, ref int effect);
    }

    const int DROPEFFECT_COPY = 1;
    const short CF_HDROP = 15;
    const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, SS_NOTIFY = 0x100, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern void OleUninitialize();
    [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDropTarget target);
    [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFileW(IntPtr drop, uint index, StringBuilder? file, int size);
}
