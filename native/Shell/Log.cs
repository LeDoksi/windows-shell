namespace WindowsShell;

/// Журнал прототипа: `%APPDATA%\WindowsShellNative\log.txt`. Пишет шаги запуска и любые
/// необработанные ошибки, чтобы падение на чужом ПК было видно без отладчика.
public static class Log
{
    static readonly string File = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsShellNative", "log.txt");
    static readonly object Gate = new();

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
        catch
        {
            // Журнал не должен ронять оболочку.
        }
    }

    public static void Error(string where, Exception e) =>
        Write($"ОШИБКА в {where}: {e.GetType().FullName} 0x{e.HResult:X8} {e.Message}{Environment.NewLine}{e}");
}
