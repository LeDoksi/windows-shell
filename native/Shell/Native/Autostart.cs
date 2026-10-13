using Microsoft.Win32;

namespace WindowsShell.Native;

/// Автозапуск с Windows (WSH-40): строка в списке автозагрузки пользователя
/// (HKCU\…\Run), без прав администратора. Там же её видно в диспетчере задач на вкладке
/// «Автозагрузка»; если её выключили там, Windows отмечает это в StartupApproved\Run,
/// и переключатель в наших настройках тоже показывает «выключено».
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string Name = "WindowsShellNative";

    /// Аргумент, с которым Windows запускает оболочку при входе.
    public const string Arg = "--autostart";

    static string Command => $"\"{Environment.ProcessPath}\" {Arg}";

    public static bool IsOn
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(Name) is not string) return false;
                return !DisabledInTaskManager();
            }
            catch (Exception e)
            {
                Log.Error("автозапуск: чтение", e);
                return false;
            }
        }
    }

    /// Первый байт записи StartupApproved: 2 или 6 — включено, 3 (и прочие нечётные) — выключено.
    static bool DisabledInTaskManager()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(Name) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;
    }

    public static void Set(bool on)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                run.SetValue(Name, Command);
                // Выключили в диспетчере задач, а теперь включают у нас: снимаем ту отметку.
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                if (approved?.GetValue(Name) is not null) approved.DeleteValue(Name, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(Name, throwOnMissingValue: false);
            }
            Log.Write($"автозапуск: {(on ? "включён" : "выключен")}");
        }
        catch (Exception e)
        {
            Log.Error("автозапуск: запись", e);
        }
    }

    /// Оболочку перенесли в другую папку (новая сборка), а автозапуск смотрит на старую:
    /// поправляем путь при каждом запуске.
    public static void Refresh()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue(Name) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            {
                run.SetValue(Name, Command);
                Log.Write($"автозапуск: путь обновлён на {Environment.ProcessPath}");
            }
        }
        catch (Exception e)
        {
            Log.Error("автозапуск: обновление пути", e);
        }
    }
}
