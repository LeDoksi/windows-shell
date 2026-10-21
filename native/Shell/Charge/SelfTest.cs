using System.Text;

namespace WindowsShell.Charge;

/// Проверка заряда без интерфейса (для проверки через удалённый доступ):
/// `WindowsShellNative.exe --charge-test`. Что нашлось в Windows (Bluetooth) и у устройств
/// Logitech (HID++), с подробностями, и оба сигнала. Итог — в `charge-test.txt` рядом с exe.
public static class SelfTest
{
    public const string Arg = "--charge-test";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        void Say(string line)
        {
            lock (report) report.AppendLine(line);
            Log.Write($"проверка заряда: {line}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Run(Say).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Say($"ОШИБКА: {e}");
        }
        Say("ГОТОВО");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "charge-test.txt"), report.ToString());
        return true;
    }

    static async Task Run(Action<string> say)
    {
        say("== Bluetooth (Windows)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bt = await Bluetooth.Read(say);
        say($"   прочитано за {sw.ElapsedMilliseconds} мс");
        foreach (var d in bt) say($"   → {Line(d)}");

        say("== Logitech (HID++)");
        sw.Restart();
        var logi = Logitech.Read(say);
        say($"   прочитано за {sw.ElapsedMilliseconds} мс");
        foreach (var d in logi) say($"   → {Line(d)}");

        say("== Сигналы");
        foreach (var name in new[] { "charge-20.mp3", "charge-10.mp3" })
        {
            var file = Path.Combine(AppContext.BaseDirectory, "Sounds", name);
            if (!File.Exists(file)) { say($"   НЕТ файла {file}"); continue; }
            var done = new TaskCompletionSource<string>();
            using var player = new Windows.Media.Playback.MediaPlayer { Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(file)) };
            player.MediaEnded += (_, _) => done.TrySetResult("доиграл");
            player.MediaFailed += (_, e) => done.TrySetResult($"ошибка {e.Error} {e.ErrorMessage}");
            player.Play();
            var finished = await Task.WhenAny(done.Task, Task.Delay(5000));
            say($"   {name}: {(finished == done.Task ? done.Task.Result : "не доиграл за 5 с")}");
        }
    }

    static string Line(ChargeDevice d) =>
        $"{d.Title} · {(d.Percent is { } p ? $"{p} %" : "?")} · подключено {d.Connected} · заряжается {d.Charging} · {d.Key}";
}
