using System.Drawing.Imaging;
using System.Text;
using static WindowsShell.Native.Win32;
using D = System.Drawing;

namespace WindowsShell.Shots;

/// Проверка снимков и записи без клавиш и мыши (для проверки на компьютере через удалённый
/// доступ): `WindowsShellNative.exe --shots-test`. Снимает основной монитор, рисует все виды
/// пометок, пишет 8 секунд видео с паузой посередине и делает из него GIF. Итог — в
/// `shots-test.txt` рядом с exe и в журнале; файлы — в обычных папках снимков и записей.
public static class SelfTest
{
    public const string Arg = "--shots-test";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проверка снимков: {line}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Run(Say).GetAwaiter().GetResult();
            Say("ГОТОВО");
        }
        catch (Exception e)
        {
            Say($"ОШИБКА: {e}");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "shots-test.txt"), report.ToString());
        return true;
    }

    static async Task Run(Action<string> say)
    {
        var mon = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var bounds = MonitorInfo(mon).rcMonitor;
        say($"основной монитор {bounds.Width}×{bounds.Height} в {bounds.Left},{bounds.Top}, масштаб {MonitorScale(mon) * 100:0} %");

        // 1. Снимок экрана.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var frozen = Frozen.Capture();
        say($"весь экран {frozen.Width}×{frozen.Height} снят за {sw.ElapsedMilliseconds} мс");
        using var shot = frozen.Crop(bounds);
        var shotPath = ShotFiles.NewPath(ShotKind.Image);
        shot.Save(shotPath, ImageFormat.Png);
        say($"снимок монитора: {shotPath} ({new FileInfo(shotPath).Length / 1024} КБ)");

        // 2. Все пометки редактора.
        var w = shot.Width;
        var h = shot.Height;
        var red = D.Color.FromArgb(0xFF, 0x5B, 0x5B);
        var state = EditState.Empty with
        {
            Marks =
            [
                new RectMark(new D.RectangleF(w * 0.1f, h * 0.1f, w * 0.3f, h * 0.2f), red, 4),
                new ArrowMark(new D.PointF(w * 0.6f, h * 0.6f), new D.PointF(w * 0.42f, h * 0.32f), red, 4),
                new MarkerMark([new D.PointF(w * 0.1f, h * 0.5f), new D.PointF(w * 0.2f, h * 0.52f), new D.PointF(w * 0.35f, h * 0.5f)], D.Color.FromArgb(0xFF, 0xC8, 0x3D), 20),
                new TextMark(new D.PointF(w * 0.45f, h * 0.62f), "Проверка текста", red, 26),
                new BlurMark(new D.Rectangle((int)(w * 0.6), (int)(h * 0.1), (int)(w * 0.3), (int)(h * 0.15)), 12),
                new NumberMark(new D.PointF(w * 0.1f, h * 0.1f), 1, red, 15),
                new NumberMark(new D.PointF(w * 0.6f, h * 0.1f), 2, D.Color.FromArgb(0x4C, 0xC2, 0xFF), 15),
            ],
            Crop = new D.Rectangle(w / 20, h / 20, w * 9 / 10, h * 9 / 10),
        };
        sw.Restart();
        using var drawn = MarkPainter.Render(shot, state);
        var drawnPath = ShotFiles.NewPath(ShotKind.Image);
        drawn.Save(drawnPath, ImageFormat.Png);
        say($"пометки нарисованы за {sw.ElapsedMilliseconds} мс: {drawnPath} ({drawn.Width}×{drawn.Height})");

        // 3. Запись: середина основного монитора 1280×720, 4 с, пауза 2 с, ещё 4 с.
        var rw = Math.Min(1280, bounds.Width) & ~1;
        var rh = Math.Min(720, bounds.Height) & ~1;
        var region = new RECT { Left = bounds.Left + (bounds.Width - rw) / 2, Top = bounds.Top + (bounds.Height - rh) / 2 };
        region.Right = region.Left + rw;
        region.Bottom = region.Top + rh;
        var videoPath = ShotFiles.NewPath(ShotKind.Video);
        sw.Restart();
        var rec = await Recorder.Start(mon, bounds, region, videoPath, system: true, mic: false);
        say($"запись началась за {sw.ElapsedMilliseconds} мс");
        await Task.Delay(4000);
        rec.Paused = true;
        await Task.Delay(2000);
        rec.Paused = false;
        await Task.Delay(4000);
        var elapsed = rec.Elapsed;
        var ok = await rec.Stop();
        say($"запись {(ok ? "готова" : "НЕ ПОЛУЧИЛАСЬ")}: {videoPath}, по часам записи {elapsed.TotalSeconds:0.0} с (ждём ~8 с)");
        if (!ok) return;
        var length = await Gif.Duration(videoPath);
        say($"длина файла {length.TotalSeconds:0.0} с, {new FileInfo(videoPath).Length / 1024} КБ");

        // 4. GIF из записи.
        sw.Restart();
        var gif = await Gif.FromVideo(videoPath);
        say(gif is null ? "GIF НЕ ПОЛУЧИЛСЯ" : $"GIF за {sw.ElapsedMilliseconds} мс: {gif} ({new FileInfo(gif).Length / 1024} КБ)");

        // 5. История: все наши файлы видны.
        var items = ShotFiles.List();
        say($"в истории {items.Count}: {string.Join(", ", items.Take(6).Select(i => i.Name))}");
    }
}
