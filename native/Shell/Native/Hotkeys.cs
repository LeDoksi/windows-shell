using System.Runtime.InteropServices;

namespace WindowsShell.Native;

/// Глобальные горячие клавиши (как `hotkeys.rs`): строки вида `Ctrl+Alt+1`, `Win+F9`.
public sealed class Hotkeys
{
    readonly MessageWindow _window;
    readonly Dictionary<int, Action> _actions = [];
    int _next;

    /// `firstId` — свой диапазон номеров: у каждого набора клавиш на одном окне сообщений свои.
    public Hotkeys(MessageWindow window, int firstId = 1)
    {
        _window = window;
        _next = firstId;
        window.Hotkey += id => { if (_actions.TryGetValue(id, out var a)) a(); };
    }

    /// Снять все и зарегистрировать заново (настройки поменялись).
    public void Set(IEnumerable<(string Hotkey, Action Action)> keys)
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_window.Hwnd, id);
        _actions.Clear();
        foreach (var (hotkey, action) in keys)
        {
            if (Parse(hotkey) is not { } k) continue;
            var id = _next++;
            if (RegisterHotKey(_window.Hwnd, id, k.Mods | MOD_NOREPEAT, k.Vk)) _actions[id] = action;
            else Log.Write($"горячая клавиша {hotkey} занята");
        }
    }

    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
    };

    public static (uint Mods, uint Vk)? Parse(string? hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return null;
        uint mods = 0, vk = 0;
        foreach (var part in hotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win": mods |= MOD_WIN; continue;
            }
            if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) vk = char.ToUpperInvariant(part[0]);
            else if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 24) vk = (uint)(0x6F + f);
            else if (Named.TryGetValue(part, out var n)) vk = n;
            else return null;
        }
        return vk == 0 ? null : (mods, vk);
    }

    /// Строка хоткея из нажатия (для поля ввода в настройках). null — ждём клавишу или не берём.
    public static string? Format(bool ctrl, bool alt, bool shift, bool win, uint vk)
    {
        string? key = vk switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x60 and <= 0x69 => ((char)('0' + vk - 0x60)).ToString(),
            >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
            _ => Named.FirstOrDefault(kv => kv.Value == vk).Key,
        };
        if (key is null) return null;
        var mods = string.Join("+", new[] { ctrl ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, win ? "Win" : null }.Where(m => m != null));
        // Обычная клавиша без модификатора отняла бы её у всех программ.
        if (mods.Length == 0 && !(key.Length > 1 && key[0] == 'F')) return null;
        return mods.Length > 0 ? $"{mods}+{key}" : key;
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
}
