using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace WindowsShell.Native;

/// Отчёты о сбоях (WSH-101): папка оболочки `crashes`. Каждый сбой или зависание — текстовый
/// отчёт (что случилось, где стоял каждый поток .NET, хвост журнала), при зависании и исключении
/// ещё снимок памяти процесса (.dmp). Пишет сторож, пока процесс оболочки ещё жив.
/// Хранятся последние 5 отчётов и 3 снимка (снимок весит сотни мегабайт).
public static class CrashReports
{
    public static readonly string Dir = Path.Combine(Config.ShellConfig.Dir, "crashes");

    public static void Write(string what, int pid, bool stacks, bool dump)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            var sb = new StringBuilder();
            sb.AppendLine($"Windows Shell: {what}");
            sb.AppendLine($"время: {DateTime.Now:dd.MM.yyyy HH:mm:ss}, процесс {pid}");
            sb.AppendLine($"сборка: {Environment.ProcessPath} от {File.GetLastWriteTime(Environment.ProcessPath!):dd.MM.yyyy HH:mm}");
            if (dump)
            {
                // Снимок первым: пока снимаем стеки, процесс может сдвинуться.
                var file = Path.Combine(Dir, name + ".dmp");
                sb.AppendLine(WriteDump(pid, file) is { } error ? $"снимок памяти не записан: {error}" : $"снимок памяти: {Path.GetFileName(file)}");
            }
            if (stacks) sb.Append(Stacks(pid));
            sb.AppendLine();
            sb.AppendLine("---- журнал, последние строки");
            sb.AppendLine(Log.Tail(80));
            var txt = Path.Combine(Dir, name + ".txt");
            File.WriteAllText(txt, sb.ToString());
            Log.Write($"отчёт о сбое: {txt}");
            Prune("*.txt", 5);
            Prune("*.dmp", 3);
        }
        catch (Exception e)
        {
            Log.Error("отчёт о сбое", e);
        }
    }

    static void Prune(string pattern, int keep)
    {
        foreach (var old in Directory.GetFiles(Dir, pattern).OrderDescending().Skip(keep))
            try { File.Delete(old); } catch { }
    }

    /// Где стоит каждый поток .NET (ClrMD по копии процесса, сам процесс не трогаем).
    static string Stacks(int pid)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---- потоки .NET (поток 1 — интерфейс)");
        try
        {
            using var target = DataTarget.CreateSnapshotAndAttach(pid);
            using var runtime = target.ClrVersions[0].CreateRuntime();
            foreach (var thread in runtime.Threads.Where(t => t.IsAlive).OrderBy(t => t.ManagedThreadId))
            {
                var frames = thread.EnumerateStackTrace().Take(60).ToList();
                if (frames.Count == 0) continue;
                sb.AppendLine($"поток {thread.ManagedThreadId} (системный {thread.OSThreadId})");
                foreach (var f in frames)
                    sb.AppendLine($"    {f.Method?.Signature ?? $"[{f.Kind}] {f.FrameName}"}");
            }
        }
        catch (Exception e)
        {
            sb.AppendLine($"(не снять: {e.GetType().Name} {e.Message})");
        }
        return sb.ToString();
    }

    /// null — записан, иначе текст ошибки.
    static string? WriteDump(int pid, string file)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            using var fs = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            // Как `dotnet-dump --type Heap`: кучи .NET и стеки есть, картинки и код библиотек — нет.
            const uint type = 0x1 /* DataSegs */ | 0x4 /* HandleData */ | 0x20 /* UnloadedModules */ | 0x200 /* PrivateReadWriteMemory */
                | 0x800 /* FullMemoryInfo */ | 0x1000 /* ThreadInfo */ | 0x20000 /* IgnoreInaccessibleMemory */;
            if (MiniDumpWriteDump(p.Handle, (uint)pid, fs.SafeFileHandle, type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)) return null;
            var error = Marshal.GetLastWin32Error();
            fs.Close();
            File.Delete(file);
            return $"ошибка {error}";
        }
        catch (Exception e)
        {
            try { File.Delete(file); } catch { }
            return e.Message;
        }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    static extern bool MiniDumpWriteDump(IntPtr process, uint pid, Microsoft.Win32.SafeHandles.SafeFileHandle file, uint type, IntPtr exception, IntPtr user, IntPtr callback);
}
