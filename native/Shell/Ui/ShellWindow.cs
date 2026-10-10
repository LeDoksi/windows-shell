using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WindowsShell.Native;
using WinUIEx;
using static WindowsShell.Native.Win32;

namespace WindowsShell.Ui;

public enum Surface { Dock, Menu, Transparent }

/// «Стекло» (размытие) или «Тонировка» (ровная тёмная заливка): выбирается вручную в меню трея.
public static class Glass
{
    public static bool Tint { get; private set; }
    public static event Action? Changed;

    public static void Set(bool tint)
    {
        if (Tint == tint) return;
        Tint = tint;
        Changed?.Invoke();
    }
}

/// Окно оболочки без рамки и заголовка, поверх всех, не в Alt+Tab и не в панели задач.
public sealed class ShellWindow
{
    public Window Window { get; }
    public IntPtr Hwnd { get; }
    public AppWindow AppWindow => Window.AppWindow;
    public Grid Root { get; }
    public bool Visible { get; private set; }

    readonly Surface _surface;

    /// <param name="surface">Стекло дока, стекло меню или прозрачное окно (полоса с иконками).</param>
    /// <param name="noActivate">Окно никогда не становится активным (док, список окон).</param>
    /// <param name="rounded">Скругление DWM 8 px с тонкой обводкой, как у меню Windows 11.</param>
    public ShellWindow(Surface surface, bool noActivate, bool rounded, bool transitions = true)
    {
        _surface = surface;
        Window = new Window();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);
        Root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            RequestedTheme = ElementTheme.Dark,
        };
        Window.Content = Root;
        ApplySurface();
        // «Стекло / Тонировка» переключается сразу во всех окнах, без перезапуска.
        Glass.Changed += ApplySurface;
        Window.Closed += (_, _) => Glass.Changed -= ApplySurface;

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        AddExStyle(Hwnd, WS_EX_TOOLWINDOW | (noActivate ? WS_EX_NOACTIVATE : 0));
        RemoveFrame(Hwnd);
        SetDark(Hwnd);
        if (rounded)
        {
            SetCorner(Hwnd, DWMWCP_ROUND);
            // Обводка едва заметная, как у меню Windows 11 в тёмной теме.
            SetBorderColor(Hwnd, Theme.ColorRef(0x30, 0x30, 0x30));
        }
        else
        {
            SetCorner(Hwnd, DWMWCP_DONOTROUND);
            SetBorderColor(Hwnd, DWMWA_COLOR_NONE);
        }
        if (!transitions) NoTransitions(Hwnd);
    }

    void ApplySurface()
    {
        switch (_surface)
        {
            case Surface.Transparent:
                Window.SystemBackdrop ??= new TransparentTintBackdrop();
                break;
            case var s when Glass.Tint:
                Window.SystemBackdrop = null;
                Root.Background = Theme.Brush(s == Surface.Dock ? Theme.TintDock : Theme.TintMenu);
                break;
            default:
                if (Window.SystemBackdrop is not AlwaysActiveAcrylic)
                    Window.SystemBackdrop = new AlwaysActiveAcrylic(dock: _surface == Surface.Dock);
                Root.Background = Theme.Brush(Microsoft.UI.Colors.Transparent);
                break;
        }
    }

    /// Масштаб монитора окна (1,0 = 100 %, 1,75 = 175 %).
    public double Scale => GetDpiForWindow(Hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;

    /// Положение и размер в физических пикселях экрана.
    public void Place(int x, int y, int w, int h)
    {
        SetWindowPos(Hwnd, HWND_TOPMOST, x, y, Math.Max(1, w), Math.Max(1, h), SWP_NOACTIVATE);
    }

    /// Быстрое перемещение без смены порядка окон (каждый кадр анимации).
    public void Move(int x, int y, int w, int h)
    {
        SetWindowPos(Hwnd, IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, h), SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
    }

    public void Show(bool activate)
    {
        if (activate)
        {
            AppWindow.Show(true);
            SetForegroundWindow(Hwnd);
        }
        else
        {
            AppWindow.Show(false);
        }
        Visible = true;
    }

    public void Hide()
    {
        AppWindow.Hide();
        Visible = false;
    }

    public void Raise() => SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
