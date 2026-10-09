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
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // Второй экземпляр не нужен: два дока друг на друге.
        using var mutex = new Mutex(true, "WindowsShellNative.Prototype", out var first);
        if (!first) return;
        Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = p;
            new App();
        });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        var queue = DispatcherQueue.GetForCurrentThread();
        var config = PrototypeConfig.Load();
        _tracker = new WindowTracker(queue);
        var audio = new AudioService(queue);
        var icons = new Icons(queue);
        _tracker.Start();
        _dock = new Dock.Dock(_tracker, audio, icons, config);
        _dock.Show();
    }

    public IXamlType GetXamlType(Type type) => _metadata.GetXamlType(type);
    public IXamlType GetXamlType(string fullName) => _metadata.GetXamlType(fullName);
    public XmlnsDefinition[] GetXmlnsDefinitions() => _metadata.GetXmlnsDefinitions();
}
