using System.Runtime.InteropServices;
using System.Text;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;
using D = System.Drawing;

namespace WindowsShell.Clips;

/// Проверка истории буфера без клавиш и мыши (для проверки через удалённый доступ):
/// `WindowsShellNative.exe --clips-test`. Кладёт в буфер текст с оформлением, картинку и
/// файл, проверяет, что они читаются, повторы не плодятся, «пароли» не запоминаются,
/// 3 дня соблюдаются, закреплённое переживает перезапуск, а Ctrl+V от оболочки вставляет
/// в своё тестовое поле ввода. История — во временной папке; работающая оболочка пометку
/// проверки видит и ничего из этого не запоминает. В конце буфер возвращается как был.
/// Итог — в `clips-test.txt` рядом с exe и в журнале.
public static class SelfTest
{
    public const string Arg = "--clips-test";

    public static bool Cli(string[] args)
    {
        if (!args.Contains(Arg)) return false;
        var report = new StringBuilder();
        var failed = 0;
        void Say(string line)
        {
            report.AppendLine(line);
            Log.Write($"проверка буфера: {line}");
        }
        void Check(bool ok, string what)
        {
            if (!ok) failed++;
            Say($"{(ok ? "ок  " : "НЕТ ")} {what}");
        }
        Say($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var dir = Path.Combine(Path.GetTempPath(), "WindowsShell-clips-test");
        ClipSnapshot? original = null;
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var messages = new MessageWindow();
            Shots.Clip.Owner = messages.Hwnd;
            original = Board.Read(test: false, out _);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Run(messages, dir, Say, Check);
        }
        catch (Exception e)
        {
            failed++;
            Say($"ОШИБКА: {e}");
        }
        finally
        {
            try { Restore(original); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        Say(failed == 0 ? "ГОТОВО: всё в порядке" : $"ГОТОВО: не прошло {failed}");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "clips-test.txt"), report.ToString());
        return true;
    }

    static void Run(MessageWindow messages, string dir, Action<string> say, Action<bool, string> check)
    {
        var store = new ClipStore(dir);
        store.Load();

        // 1. Слушатель: Windows сообщает об изменении буфера.
        var updates = 0;
        messages.Message += (msg, _, _) => { if (msg == 0x031D) updates++; };
        check(AddClipboardFormatListener(messages.Hwnd), "подписка на изменения буфера");

        // 2. Текст с оформлением.
        const string text = "Проверка буфера обмена\r\nвторая строка";
        var html = Encoding.UTF8.GetBytes("Version:0.9\r\nStartHTML:0\r\n<b>Проверка</b>");
        var rtf = Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\b Proverka}}");
        check(Board.PutText(text, html, rtf, test: true), "положить текст с оформлением");
        Pump(300);
        check(updates > 0, $"сообщение об изменении пришло ({updates})");
        var snap = Board.Read(test: true, out var skip);
        check(snap?.Text == text && snap.Html != null && snap.Rtf != null, $"текст прочитан с HTML и RTF{(skip != null ? $" ({skip})" : "")}; программа: {snap?.Source ?? "?"}");
        var textEntry = snap is null ? null : store.Prepare(snap);
        if (textEntry != null) textEntry = store.Commit(textEntry);
        check(textEntry is { Kind: ClipKind.Text, HasHtml: true, HasRtf: true }, "пункт «текст» в истории");

        // 3. Тот же текст без оформления — не новый пункт, а тот же наверху.
        Board.PutText(text, null, null, test: true);
        if (Board.Read(test: true, out _) is { } again && store.Prepare(again) is { } dup) store.Commit(dup);
        check(store.History.Count == 1, $"повтор не плодится (в истории {store.History.Count})");

        // 4. Картинка.
        using (var bmp = new D.Bitmap(320, 200))
        {
            using (var g = D.Graphics.FromImage(bmp))
            {
                g.Clear(D.Color.FromArgb(0x4C, 0xC2, 0xFF));
                g.FillEllipse(D.Brushes.White, 100, 40, 120, 120);
            }
            using var png = new MemoryStream();
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            check(Board.PutImage(png.ToArray(), test: true), "положить картинку");
        }
        var imgSnap = Board.Read(test: true, out _);
        var image = imgSnap is null ? null : store.Prepare(imgSnap);
        if (image != null) image = store.Commit(image);
        check(image is { Kind: ClipKind.Image, Width: 320, Height: 200 }, $"картинка в истории ({image?.Width}×{image?.Height})");

        // 4б. Картинка только как DIB (как из Paint): переводится в PNG.
        using (var bmp = new D.Bitmap(64, 48))
        {
            using (var g = D.Graphics.FromImage(bmp)) g.Clear(D.Color.OrangeRed);
            Board.Put([(8 /* CF_DIB */, Shots.Clip.Dib(bmp))], test: true);
        }
        var dibSnap = Board.Read(test: true, out _);
        var dibEntry = dibSnap is null ? null : store.Prepare(dibSnap);
        check(dibEntry is { Kind: ClipKind.Image, Width: 64, Height: 48 }, $"картинка DIB → PNG ({dibEntry?.Width}×{dibEntry?.Height})");
        if (dibEntry != null) store.Discard(dibEntry);

        // 5. Файлы.
        var file = Path.Combine(dir, "проверка.txt");
        File.WriteAllText(file, "файл для проверки");
        check(Board.PutFiles([file], test: true), "положить файл");
        var fileSnap = Board.Read(test: true, out _);
        var files = fileSnap is null ? null : store.Prepare(fileSnap);
        if (files != null) files = store.Commit(files);
        check(files is { Kind: ClipKind.Files } && files.Files![0] == file, "файл в истории");

        // 6. «Пароли»: помеченное менеджером паролей не запоминается.
        Board.Put([(13, Encoding.Unicode.GetBytes("пароль123\0"))], test: true, Board.ExcludeMark);
        check(Board.Read(test: true, out var why1) is null, $"пометка ExcludeClipboardContentFromMonitorProcessing — не запомнили ({why1})");
        Board.Put([(13, Encoding.Unicode.GetBytes("пароль456\0"))], test: true, Board.NoHistoryMark);
        check(Board.Read(test: true, out var why2) is null, $"пометка CanIncludeInClipboardHistory=0 — не запомнили ({why2})");
        Board.PutText("проверка", null, null, test: true);
        check(Board.Read(test: false, out var why3) is null, $"работающая оболочка проверку не запоминает ({why3})");

        // 7. Обратно в буфер: с оформлением и без.
        if (textEntry != null)
        {
            Board.PutText(store.ReadText(textEntry), store.ReadBytes(textEntry, ".html"), store.ReadBytes(textEntry, ".rtf"), test: true);
            var back = Board.Read(test: true, out _);
            check(back?.Text == text && back.Html != null, "вставка с оформлением: текст и HTML вернулись");
            Board.PutText(store.ReadText(textEntry), null, null, test: true);
            back = Board.Read(test: true, out _);
            check(back?.Text == text && back.Html is null && back.Rtf is null, "вставка без оформления: только текст");
        }
        if (image != null && store.ReadBytes(image, ".png") is { } imgBytes)
        {
            Board.PutImage(imgBytes, test: true);
            var back = Board.Read(test: true, out _);
            check(back?.Png != null && Board.PngSize(back.Png) == (320, 200), "картинка вернулась в буфер");
        }

        // 8. Закрепить, порядок, 3 дня, перезапуск.
        if (textEntry != null && image != null && files != null)
        {
            store.SetPinned(textEntry, true);
            store.SetPinned(image, true);
            store.MovePinned(image, 0);
            check(store.Pinned.Select(e => e.Id).SequenceEqual([image.Id, textEntry.Id]), "закреплённые: порядок перетаскиванием");
            files.Time = DateTime.Now - TimeSpan.FromDays(4);
            textEntry.Time = DateTime.Now - TimeSpan.FromDays(4);
            store.Trim();
            check(store.History.Count == 0 && store.Pinned.Count == 2, "старше 3 дней ушло, закреплённое осталось");
            var reloaded = new ClipStore(dir);
            reloaded.Load();
            check(reloaded.Pinned.Select(e => e.Id).SequenceEqual([image.Id, textEntry.Id]) && reloaded.Pinned[1].Text == text, "после перезапуска закреплённое на месте");
            store.ClearHistory();
            store.SetPinned(textEntry, false);
            check(store.History.Count == 1 && store.Pinned.Count == 1, "открепили — вернулось в историю");
        }
        RemoveClipboardFormatListener(messages.Hwnd);

        // 9. Вставка Ctrl+V в своё поле ввода (только если оно правда активно: в чужое окно не печатаем).
        PasteTest(say, check);
    }

    static void PasteTest(Action<string> say, Action<bool, string> check)
    {
        const string sample = "вставка из оболочки";
        var edit = CreateWindowExW(0, "EDIT", "", 0x00CF0000 /* WS_OVERLAPPEDWINDOW */ | 0x10000000 /* WS_VISIBLE */ | 0x4 /* ES_MULTILINE */,
            200, 200, 520, 220, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (edit == IntPtr.Zero) { check(false, "тестовое поле ввода не создалось"); return; }
        try
        {
            SetWindowTextW(edit, "");
            WinKeys.SendMask();
            Pump(50);
            SetForegroundWindow(edit);
            SetFocus(edit);
            Pump(300);
            var caret = Caret.Find(edit);
            check(caret != null, $"текстовый курсор найден: {(caret is { } c ? $"{c.X:0},{c.Y:0} {c.Width:0}×{c.Height:0}" : "нет")}");
            if (GetForegroundWindow() != edit)
            {
                say("Ctrl+V не проверяли: Windows не дала сделать тестовое поле активным (так бывает при удалённом запуске)");
                return;
            }
            Board.PutText(sample, null, null, test: true);
            WinKeys.SendPaste();
            Pump(400);
            var sb = new StringBuilder(256);
            GetWindowTextW(edit, sb, sb.Capacity);
            check(sb.ToString() == sample, $"Ctrl+V от оболочки вставил текст («{sb}»)");
        }
        finally
        {
            DestroyWindow(edit);
        }
    }

    /// Вернуть буфер, каким он был до проверки (текст, картинку или файлы).
    static void Restore(ClipSnapshot? s)
    {
        if (s is null) return;
        if (s.Files != null) Board.PutFiles(s.Files);
        else if (s.Text != null) Board.PutText(s.Text, s.Html, s.Rtf);
        else if (s.Png != null) Board.PutImage(s.Png);
        else if (s.Dib != null) Board.Put([(8, s.Dib)], test: false);
    }

    /// Покрутить очередь сообщений `ms` миллисекунд.
    static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(10);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr h, string text);
}
