using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;
using WindowsShell.Apps;
using WindowsShell.Audio;
using WindowsShell.Config;

namespace WindowsShell;

/// Прототип оболочки на WinUI 3 (WSH-E13): док с увеличением, родное меню, окошко звука.
/// Интерфейс собран кодом, без .xaml, поэтому метаданные контролов берём у WinUI напрямую.
public sealed class App : Application, IXamlMetadataProvider
{
    readonly XamlControlsXamlMetaDataProvider _metadata = new();
    WindowTracker? _tracker;
    Dock.Dock? _dock;

    [STAThread]
    public static void Main()
    {
        Log.Write("---- запуск");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"ОШИБКА (домен): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("задача", e.Exception);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Второй экземпляр не нужен: два дока друг на друге.
        using var mutex = new Mutex(true, "WindowsShellNative.Prototype", out var first);
        if (!first) return;
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
            var config = PrototypeConfig.Load();
            step = "трекер окон";
            _tracker = new WindowTracker(queue);
            step = "звук";
            var audio = new AudioService(queue);
            var icons = new Icons(queue);
            _tracker.Start();
            step = "окна дока";
            _dock = new Dock.Dock(_tracker, audio, icons, config);
            step = "показ дока";
            _dock.Show();
            Log.Write("док показан");
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
