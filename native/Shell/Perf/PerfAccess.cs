using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WindowsShell.Perf;

/// Право читать события показа кадров (ETW): админ или группа «Пользователи журналов
/// производительности» (S-1-5-32-559). Оболочка работает без админа, поэтому один раз добавляем
/// пользователя в группу (окно UAC). Windows видит новую группу после следующего входа.
public static class PerfAccess
{
    const string GroupSid = "S-1-5-32-559";
    public const string GrantArg = "--perf-grant";

    public enum State { Ready, AfterSignIn, Needed }

    /// Есть ли право сейчас, дано ли (ждёт входа), или нужно дать.
    public static State Current
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                var p = new WindowsPrincipal(id);
                if (p.IsInRole(new SecurityIdentifier(GroupSid)) || p.IsInRole(WindowsBuiltInRole.Administrator)) return State.Ready;
                return IsMember(id.Name) ? State.AfterSignIn : State.Needed;
            }
            catch (Exception e)
            {
                Log.Error("права FPS", e);
                return State.Needed;
            }
        }
    }

    /// Имя группы на языке Windows.
    public static string GroupName
    {
        get
        {
            try { return new SecurityIdentifier(GroupSid).Translate(typeof(NTAccount)).Value.Split('\\')[^1]; }
            catch { return "Performance Log Users"; }
        }
    }

    /// Пользователь уже в группе (по учётной записи, а не по текущему входу).
    static bool IsMember(string user)
    {
        var group = GroupName;
        if (NetUserGetLocalGroups(null, user, 0, 1 /* LG_INCLUDE_INDIRECT */, out var buf, -1, out var read, out _) != 0) return false;
        try
        {
            for (var i = 0; i < read; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, i * IntPtr.Size));
                if (string.Equals(name, group, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { NetApiBufferFree(buf); }
    }

    /// Попросить права администратора (окно UAC) и добавить себя в группу. true — получилось.
    public static async Task<bool> Grant()
    {
        string sid;
        using (var id = WindowsIdentity.GetCurrent()) sid = id.User!.Value;
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"{GrantArg} {sid}") { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            Log.Write($"права FPS: добавление в группу — код {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            Log.Write("права FPS: окно администратора закрыли");
            return false;
        }
        catch (Exception e)
        {
            Log.Error("права FPS", e);
            return false;
        }
    }

    /// `--perf-grant <sid>`: уже с правами администратора добавить пользователя в группу.
    public static bool Cli(string[] args)
    {
        var i = Array.IndexOf(args, GrantArg);
        if (i < 0) return false;
        var code = 87; // ERROR_INVALID_PARAMETER
        try
        {
            var user = new SecurityIdentifier(args[i + 1]);
            var bytes = new byte[user.BinaryLength];
            user.GetBinaryForm(bytes, 0);
            var psid = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, psid, bytes.Length);
                var info = new LOCALGROUP_MEMBERS_INFO_0 { lgrmi0_sid = psid };
                code = NetLocalGroupAddMembers(null, GroupName, 0, ref info, 1);
                if (code == 1378) code = 0; // ERROR_MEMBER_IN_ALIAS: уже в группе
            }
            finally { Marshal.FreeHGlobal(psid); }
        }
        catch (Exception e) { Log.Error("права FPS (администратор)", e); }
        Log.Write($"права FPS: {GroupName} — код {code}");
        Environment.ExitCode = code;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] struct LOCALGROUP_MEMBERS_INFO_0 { public IntPtr lgrmi0_sid; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetLocalGroupAddMembers(string? server, string group, int level, ref LOCALGROUP_MEMBERS_INFO_0 buf, int count);
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] static extern int NetUserGetLocalGroups(string? server, string user, int level, int flags, out IntPtr buf, int max, out int read, out int total);
    [DllImport("netapi32.dll")] static extern int NetApiBufferFree(IntPtr buf);
}
