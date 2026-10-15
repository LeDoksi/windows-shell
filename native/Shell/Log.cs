namespace WindowsShell;

/// Журнал прототипа: `%APPDATA%\WindowsShellNative\log.txt`. Пишет шаги запуска и любые
/// необработанные ошибки, чтобы падение на чужом ПК было видно без отладчика.
public static class Log
{
    static readonly string File = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsShellNative", "log.txt");
    static readonly object Gate = new();
    // Запасной журнал, если в основной не пишется (папку оболочки не дают открыть, WSH-99).
    static readonly string Fallback = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WindowsShellNative-log.txt");

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:HH:mm:ss.fff} {text}{Environment.NewLine}");
            }
        }
        catch (Exception e)
        {
            // Журнал не должен ронять оболочку.
            try { System.IO.File.AppendAllText(Fallback, $"{DateTime.Now:dd.MM HH:mm:ss.fff} {text} [основной журнал: {e.GetType().Name} {e.Message}]{Environment.NewLine}"); }
            catch { }
        }
    }

    public static void Error(string where, Exception e) =>
        Write($"ОШИБКА в {where}: {e.GetType().FullName} 0x{e.HResult:X8} {e.Message}{Environment.NewLine}{e}");
}
