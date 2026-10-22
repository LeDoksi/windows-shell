using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using WindowsShell.Apps;
using WindowsShell.Audio;
using WindowsShell.Config;
using WindowsShell.Media;
using WindowsShell.Native;

namespace WindowsShell;

/// Оболочка на WinUI 3 (WSH-E14): док на каждом мониторе, Пуск, меню и окошки
/// со стеклом, виджеты. Интерфейс собран кодом, без .xaml, поэтому метаданные контролов
/// берём у WinUI напрямую.
public sealed class App : Application, IXamlMetadataProvider
{
    readonly XamlControlsXamlMetaDataProvider _metadata = new();
    ShellHost? _host;

    [STAThread]
    public static void Main(string[] args)
    {
        // Аварийный возврат панели задач, сторож, ярлык и остановка: без окон и без интерфейса.
        if (Native.Taskbar.Cli(args)) return;
        if (Native.Watchdog.Cli(args)) return;
        if (Shots.SelfTest.Cli(args)) return;
        if (Clips.SelfTest.Cli(args)) return;
        if (Charge.SelfTest.Cli(args)) return;
        if (Perf.SelfTest.Cli(args)) return;
        if (Perf.PerfAccess.Cli(args)) return;
        Log.Write(args.Contains(Native.Autostart.Arg) ? "---- запуск вместе с Windows"
            : args.Contains(Native.Watchdog.RecoveredArg) ? "---- запуск после сбоя (сторож)" : "---- запуск");
        // Без отчёта Windows об ошибке: он держит упавший процесс ещё секунд 7, и сторож дольше
        // не может поднять оболочку. Отчёт пишет сам сторож.
        Native.Watchdog.NoErrorReporting();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write($"ОШИБКА (домен): {e.ExceptionObject}");
            // Пока процесс жив, сторож снимает стеки; потом закрываемся сразу, без Windows.
            Native.Watchdog.DumpMe();
            Native.Watchdog.DieNow();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("задача", e.Exception);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Второй экземпляр не нужен: два дока друг на друге. После «Перезапустить» ждём,
        // пока прежний процесс закончит выход.
        using var mutex = new Mutex(false, "WindowsShellNative.Shell");
        bool owned;
        try { owned = mutex.WaitOne(args.Contains(Native.Watchdog.RestartArg) ? 10_000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            // Ярлык «Windows Shell», а сторожа нет (оболочку запустила старая сборка): просим перезапуститься.
            if (args.Contains(Native.Watchdog.LaunchArg)) Native.Watchdog.PostShell(Native.Watchdog.ShellRestart);
            return;
        }
        Native.Watchdog.Attach();
        if (args.Contains(Native.Autostart.Arg)) WaitForExplorer();
        Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = p;
            var app = new App();
            app.UnhandledException += (_, e) => { Log.Error("XAML", e.Exception); e.Handled = true; };
        });
    }

    /// Задача планировщика запускает оболочку сразу при входе, раньше Проводника. Трею и резерву
    /// места (AppBar) нужен Проводник, поэтому ждём его окно панели задач (не дольше минуты).
    static void WaitForExplorer()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (FindWindowW("Shell_TrayWnd", null) == IntPtr.Zero && sw.ElapsedMilliseconds < 60_000) Thread.Sleep(200);
        Log.Write($"Проводник готов через {sw.ElapsedMilliseconds} мс");
        // Пусть Проводник успеет создать трей и рабочие области мониторов.
        Thread.Sleep(500);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindowW(string cls, string? title);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var step = "ресурсы";
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            var queue = DispatcherQueue.GetForCurrentThread();
            step = "конфиг";
            var config = ShellConfig.Load();
            Autostart.Refresh();
            // Ярлык «Windows Shell» и имя для уведомлений: в фоне, запуск не ждёт.
            Task.Run(() => { Toasts.Register(); Shortcuts.Refresh(); });
            step = "окно сообщений";
            var messages = new MessageWindow();
            var monitors = new Monitors(queue, messages);
            step = "трекер окон";
            var tracker = new WindowTracker(queue);
            step = "звук";
            var audio = new AudioService(queue);
            var tray = new Tray.TrayHost(queue);
            var bin = new Tail.RecycleBin(queue, messages);
            var services = new Services
            {
                Queue = queue,
                Config = config,
                Messages = messages,
                Monitors = monitors,
                Bars = new Ui.Bars(queue, monitors),
                Tracker = tracker,
                Audio = audio,
                Media = new MediaService(queue),
                Icons = new Icons(queue),
                Keys = new WinKeys(queue),
                Catalog = new Start.Catalog(queue),
                Tray = tray,
                Buttons = new Tray.TaskbarButtons(tray),
                Network = new Tail.NetworkService(queue),
                Battery = new Tail.BatteryService(queue, messages),
                Language = new Tail.InputLanguage(queue),
                Notifications = new Tail.Notifications(queue),
                Bin = bin,
                Shots = new Shots.ShotsService(queue, config, monitors, bin, messages),
                Clips = new Clips.ClipsService(queue, config, messages),
                Charge = new Charge.ChargeService(queue, config, messages),
                Perf = new Perf.PerfService(queue, config, tracker, messages),
            };
            Ui.Popup.RestoreForeground = tracker.RestoreForeground;
            tracker.Start();
            step = "док и панель";
            _host = new ShellHost(services);
            _host.Start();
            Log.Write($"оболочка запущена: мониторов {monitors.All.Count}");
        }
        catch (Exception e)
        {
            Log.Error(step, e);
            throw;
        }
    }

    public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _metadata.GetXmlnsDefinitions();
}
