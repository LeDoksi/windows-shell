using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsShell.Native;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Ui;

/// Иконка, которая едет за курсором, когда плитку тащат из окна категории в док или на
/// другую категорию: своё прозрачное окно поверх всего. Куда бросили, считаем по курсору,
/// а не по окну под ним, поэтому призрак ничему не мешает.
public sealed class DragGhost
{
    const double Size = 48;
    readonly ShellWindow _win;
    readonly Image _image = new() { Width = Size, Height = Size, Stretch = Stretch.Uniform, Opacity = 0.85 };

    public DragGhost()
    {
        _win = new ShellWindow(Surface.Transparent, noActivate: true, rounded: false, transitions: false);
        _win.Root.IsHitTestVisible = false;
        _win.Root.Children.Add(_image);
    }

    public void Show(ImageSource? icon, int x, int y)
    {
        _image.Source = icon;
        MoveTo(x, y);
        _win.Show(activate: false);
        _win.Raise();
    }

    public void MoveTo(int x, int y)
    {
        var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        var px = (int)Math.Round(Size * MonitorScale(mon));
        _win.Place(x - px / 2, y - px / 2, px, px);
    }

    public void Hide() => _win.Hide();
}
