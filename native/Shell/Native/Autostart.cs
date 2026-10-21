using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace WindowsShell.Native;

/// Автозапуск с Windows (WSH-40): задача планировщика «при входе этого пользователя», без прав
/// администратора. Строку в HKCU\…\Run на mephPC Windows молча пропустила (2026-10-13), а
/// записи Run к тому же запускаются через полминуты после появления рабочего стола. Задача
/// стартует сразу при входе; сама оболочка дожидается Проводника (App.Main).
public static class Autostart
{
    const string TaskName = "WindowsShellNative";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// Аргумент, с которым Windows запускает оболочку при входе.
    public const string Arg = "--autostart";

    static string Exe => Environment.ProcessPath!;
    static readonly string PathFile = Path.Combine(WindowsShell.Config.ShellConfig.Dir, "autostart.txt");

    /// Задача есть и включена.
    public static bool IsOn
    {
        get
        {
            var (code, xml) = Schtasks($"/query /tn \"{TaskName}\" /xml");
            return code == 0 && !xml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase) || RunValue() != null;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            RemoveRunValue();
            if (on) Create();
            else
            {
                var (code, output) = Schtasks($"/delete /tn \"{TaskName}\" /f");
                Log.Write(code == 0 ? "автозапуск: выключен" : $"автозапуск: задачу не удалось удалить ({code}): {output.Trim()}");
            }
        }
        catch (Exception e)
        {
            Log.Error("автозапуск: запись", e);
        }
    }

    /// При каждом запуске: строку из Run (первая версия) переносим в задачу; оболочку перенесли
    /// в другую папку (новая сборка) — задача смотрит на новый exe.
    public static void Refresh()
    {
        try
        {
            if (RunValue() != null)
            {
                Log.Write("автозапуск: переношу из Run в планировщик");
                Set(true);
                return;
            }
            // Какой exe прописан в задаче, помним сами: вывод schtasks в разных кодировках, а в пути кириллица.
            var registered = File.Exists(PathFile) ? File.ReadAllText(PathFile).Trim() : null;
            if (string.Equals(registered, Exe, StringComparison.OrdinalIgnoreCase)) return;
            var (code, _) = Schtasks($"/query /tn \"{TaskName}\"");
            if (code == 0)
            {
                Log.Write($"автозапуск: путь обновлён на {Exe}");
                Create();
            }
        }
        catch (Exception e)
        {
            Log.Error("автозапуск: обновление", e);
        }
    }

    static void Create()
    {
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var exe = SecurityElement.Escape(Exe);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(Exe) ?? "");
        // Обычный приоритет (4), без ограничения по времени, не останавливать от батареи.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Windows Shell: док и Пуск при входе в систему.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>{Arg}</Arguments><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "WindowsShellNative-task.xml");
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        try
        {
            var (code, output) = Schtasks($"/create /tn \"{TaskName}\" /xml \"{file}\" /f");
            if (code == 0)
            {
                Log.Write($"автозапуск: включён (задача планировщика, {Exe})");
                try { File.WriteAllText(PathFile, Exe); } catch { }
                return;
            }
            // Планировщик не дал (политика): запасной путь — строка в Run.
            Log.Write($"автозапуск: задачу не создать ({code}): {output.Trim()}; пишу в Run");
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(TaskName, $"\"{Exe}\" {Arg}");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    static string? RunValue()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(TaskName) as string;
    }

    static void RemoveRunValue()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(TaskName, throwOnMissingValue: false);
    }

    static (int Code, string Output) Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } return (-1, "timeout"); }
        return (p.ExitCode, output);
    }
}
