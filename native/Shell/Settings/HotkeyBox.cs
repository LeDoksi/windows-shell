using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WindowsShell.Native;
using WindowsShell.Ui;
using static WindowsShell.Native.Win32;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Settings;

/// Поле горячей клавиши: клик — «Нажмите сочетание…», нажал — записалось. Esc — отмена,
/// Backspace или Delete — убрать сочетание.
public sealed class HotkeyBox : Button
{
    readonly TextBlock _text = new() { FontFamily = Theme.Font, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Action<string?> _changed;
    string? _value;
    bool _capturing;

    public HotkeyBox(string? value, Action<string?> changed)
    {
        _value = value;
        _changed = changed;
        Content = _text;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Height = 32;
        Show();
        Click += (_, _) => { _capturing = true; Show(); Focus(FocusState.Programmatic); };
        LostFocus += (_, _) => { _capturing = false; Show(); };
        KeyDown += OnKey;
    }

    void Show()
    {
        _text.Text = _capturing ? "Нажмите сочетание…" : _value ?? "Не задано";
        _text.Foreground = Theme.Brush(_capturing || _value is null ? Theme.TextSecondary : Theme.Text);
    }

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                _capturing = false;
                Show();
                return;
            case VirtualKey.Back or VirtualKey.Delete:
                _capturing = false;
                _value = null;
                Show();
                _changed(null);
                return;
            case VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftMenu or VirtualKey.RightMenu:
                return;
        }
        var hotkey = Hotkeys.Format(Win32.KeyDown(VK_CONTROL), Win32.KeyDown(0x12), Win32.KeyDown(VK_SHIFT), Win32.KeyDown(VK_LWIN) || Win32.KeyDown(0x5C), (uint)e.Key);
        if (hotkey is null) return;
        _capturing = false;
        _value = hotkey;
        Show();
        _changed(hotkey);
    }
}
