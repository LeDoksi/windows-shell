using System.Security;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace WindowsShell.Native;

/// Уведомления Windows от имени «Windows Shell» (WSH-101). Программа без пакета: имя и значок
/// задаём в `HKCU\Software\Classes\AppUserModelId\<id>` (так делает и Windows App SDK), кнопки
/// открывают ссылку `windows-shell-native:` — это наш exe с `--launch`, COM-сервер не нужен.
public static class Toasts
{
    public const string Aumid = "LeDoksi.WindowsShell";
    public const string Protocol = "windows-shell-native";

    /// Имя, значок и ссылка для кнопок. Зовём при каждом запуске: exe мог переехать.
    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}"))
            {
                k.SetValue("DisplayName", "Windows Shell");
                if (IconPng() is { } png) k.SetValue("IconUri", png);
            }
            using var p = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Protocol}");
            p.SetValue("", "URL:Windows Shell");
            p.SetValue("URL Protocol", "");
            using var c = p.CreateSubKey(@"shell\open\command");
            c.SetValue("", $"\"{exe}\" {Watchdog.LaunchArg} \"%1\"");
        }
        catch (Exception e)
        {
            Log.Error("регистрация уведомлений", e);
        }
    }

    /// `button` — подпись кнопки «запустить оболочку», null — без кнопок. Клик по самому
    /// уведомлению ничего не запускает.
    public static void Show(string title, string text, bool silent, string? button = null)
    {
        try
        {
            Register();
            var actions = button is null ? "" :
                $"""<actions><action content="{SecurityElement.Escape(button)}" activationType="protocol" arguments="{Protocol}:launch"/></actions>""";
            var xml = $"""
                <toast launch="{Protocol}:none" activationType="protocol">
                  <visual><binding template="ToastGeneric"><text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(text)}</text></binding></visual>
                  {(silent ? "<audio silent=\"true\"/>" : "")}
                  {actions}
                </toast>
                """;
            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(new ToastNotification(doc));
            Log.Write($"уведомление: {title}");
        }
        catch (Exception e)
        {
            Log.Error("уведомление", e);
        }
    }

    /// Уведомлению нужна картинка: самый большой PNG из shell.ico рядом с exe → папка оболочки.
    static string? IconPng()
    {
        var png = Path.Combine(Config.ShellConfig.Dir, "shell.png");
        if (File.Exists(png)) return png;
        var ico = Path.Combine(AppContext.BaseDirectory, "shell.ico");
        if (!File.Exists(ico)) return null;
        var d = File.ReadAllBytes(ico);
        int count = BitConverter.ToUInt16(d, 4), bestSize = -1, bestOffset = 0, bestLength = 0;
        for (var i = 0; i < count; i++)
        {
            var e = 6 + 16 * i;
            var size = d[e] == 0 ? 256 : d[e];
            var length = BitConverter.ToInt32(d, e + 8);
            var offset = BitConverter.ToInt32(d, e + 12);
            var isPng = offset + 4 <= d.Length && d[offset] == 0x89 && d[offset + 1] == (byte)'P';
            if (isPng && size > bestSize) (bestSize, bestOffset, bestLength) = (size, offset, length);
        }
        if (bestSize < 0) return null;
        Directory.CreateDirectory(Config.ShellConfig.Dir);
        File.WriteAllBytes(png, d.AsSpan(bestOffset, bestLength).ToArray());
        return png;
    }
}
