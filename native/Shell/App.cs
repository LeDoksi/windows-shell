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

/// Оболочка на WinUI 3 (WSH-E14): док и верхняя панель на каждом мониторе, меню и окошки
/// со стеклом, виджеты. Интерфейс собран кодом, без .xaml, поэтому метаданные контролов
/// берём у WinUI напрямую.
public sealed class App : Application, IXamlMetadataProvider
{
    readonly XamlControlsXamlMetaDataProvider _metadata = new();
    ShellHost? _host;

    [STAThread]
    public static void Main(string[] args)
    {
        Log.Write(args.Contains(Native.Autostart.Arg) ? "---- запуск вместе с Windows" : "---- запуск");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"ОШИБКА (домен): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("задача", e.Exception);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Второй экземпляр не нужен: два дока друг на друге. После «Перезапустить» ждём,
        // пока прежний процесс закончит выход.
        using var mutex = new Mutex(false, "WindowsShellNative.Shell");
        bool owned;
        try { owned = mutex.WaitOne(args.Contains("--restart") ? 10_000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return;
        Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = p;
            var app = new App();
            app.UnhandledException += (_, e) => { Log.Error("XAML", e.Exception); e.Handled = true; };
        });
    }

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
            step = "окно сообщений";
            var messages = new MessageWindow();
            var monitors = new Monitors(queue, messages);
            step = "трекер окон";
            var tracker = new WindowTracker(queue);
            step = "звук";
            var audio = new AudioService(queue);
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
