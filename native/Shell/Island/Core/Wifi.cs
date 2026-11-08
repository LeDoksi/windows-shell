using System.Security;
using System.Text;

namespace WindowsShell.Island.Core;

public enum WifiSecurity { Open, Personal, Wpa3Personal, Enterprise }

/// Тип защиты по DOT11_AUTH_ALGORITHM из списка сетей Windows.
public static class WifiAuth
{
    public static WifiSecurity Of(int algo) => algo switch
    {
        1 or 10 => WifiSecurity.Open,          // открытая, OWE (Enhanced Open)
        4 or 7 => WifiSecurity.Personal,       // WPA-PSK, WPA2-PSK
        8 or 9 => WifiSecurity.Wpa3Personal,   // WPA3 (SAE)
        _ => WifiSecurity.Enterprise,          // 802.1X и всё незнакомое — в Параметры Windows (П15)
    };
}

/// Профиль Wi-Fi для WlanSetProfile (И5): открытая сеть или WPA2/WPA3-Personal с паролем.
public static class WifiProfile
{
    public static bool PasswordOk(WifiSecurity s, string? p) => s switch
    {
        WifiSecurity.Open => true,
        WifiSecurity.Enterprise => false,
        _ => p is { Length: >= 8 and <= 63 } || (p is { Length: 64 } && p.All(Uri.IsHexDigit)),
    };

    public static string Xml(string ssid, WifiSecurity s, string? password)
    {
        var name = SecurityElement.Escape(ssid);
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
        var (auth, enc) = s switch
        {
            WifiSecurity.Open => ("open", "none"),
            WifiSecurity.Wpa3Personal => ("WPA3SAE", "AES"),
            _ => ("WPA2PSK", "AES"),
        };
        var key = s == WifiSecurity.Open ? "" :
            $"<sharedKey><keyType>{(password is { Length: 64 } ? "networkKey" : "passPhrase")}</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password ?? "")}</keyMaterial></sharedKey>";
        return "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
            $"<name>{name}</name><SSIDConfig><SSID><hex>{hex}</hex><name>{name}</name></SSID></SSIDConfig>" +
            "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
            $"<MSM><security><authEncryption><authentication>{auth}</authentication><encryption>{enc}</encryption><useOneX>false</useOneX></authEncryption>{key}</security></MSM>" +
            "</WLANProfile>";
    }
}
