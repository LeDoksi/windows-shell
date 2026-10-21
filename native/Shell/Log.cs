namespace WindowsShell;

/// Журнал прототипа: `%APPDATA%\WindowsShellNative\log.txt`. Пишет шаги запуска и любые
/// необработанные ошибки, чтобы падение на чужом ПК было видно без отладчика.
/// В тот же файл пишет сторож (отдельный процесс), поэтому файл открываем с общим доступом.
public static class Log
{
    public static readonly string File = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsShellNative", "log.txt");
    static readonly object Gate = new();
    // Запасной журнал, если в основной не пишется (папку оболочки не дают открыть, WSH-99).
    static readonly string Fallback = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WindowsShellNative-log.txt");

    /// Приставка к каждой строке: у сторожа «сторож:», у оболочки пусто.
    public static string Prefix { get; set; } = "";

    public static void Write(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {Prefix}{text}{Environment.NewLine}";
        Exception? last = null;
        lock (Gate)
        {
            // Сторож и оболочка могут писать в одну и ту же миллисекунду: пара повторов.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
                    using var f = new FileStream(File, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    f.Write(bytes, 0, bytes.Length);
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                    Thread.Sleep(15);
                }
            }
        }
        // Журнал не должен ронять оболочку.
        try { System.IO.File.AppendAllText(Fallback, $"{DateTime.Now:dd.MM} {line.TrimEnd()} [основной журнал: {last?.GetType().Name} {last?.Message}]{Environment.NewLine}"); }
        catch { }
    }

    public static void Error(string where, Exception e) =>
        Write($"ОШИБКА в {where}: {e.GetType().FullName} 0x{e.HResult:X8} {e.Message}{Environment.NewLine}{e}");

    /// Последние строки журнала (для отчёта о сбое).
    public static string Tail(int lines)
    {
        try
        {
            using var f = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Хвоста в 64 КБ хватает на сотню строк с исключениями.
            if (f.Length > 64 * 1024) f.Seek(-64 * 1024, SeekOrigin.End);
            using var r = new StreamReader(f, System.Text.Encoding.UTF8);
            var all = r.ReadToEnd().Split('\n');
            return string.Join('\n', all.TakeLast(lines));
        }
        catch (Exception e)
        {
            return $"(журнал не прочитать: {e.Message})";
        }
    }
}
