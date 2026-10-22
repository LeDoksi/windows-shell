using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WindowsShell.Ui;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowsShell.Kanban;

/// Вход в канбан (окошко в доке, окно, настройки): Google через браузер или почта и пароль.
/// Один раз — дальше оболочка помнит вход сама.
public static class SignInView
{
    public static StackPanel Build(KanbanService kanban, double width = double.NaN)
    {
        var root = new StackPanel { Spacing = 8, Width = width };
        TextBlock Text(string t, double size = 13, bool secondary = true) => new()
        {
            Text = t, FontFamily = Theme.Font, FontSize = size, TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.Brush(secondary ? Theme.TextSecondary : Theme.Text),
        };

        if (kanban.WaitingForGoogle)
        {
            root.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { new ProgressRing { IsActive = true, Width = 18, Height = 18 }, Text("Жду вход в браузере…", 14, secondary: false) },
            });
            root.Children.Add(Text("Выбери аккаунт Google в открывшейся вкладке. Когда там появится «Готово», здесь всё откроется само."));
            var cancel = new Button { Content = "Отмена" };
            cancel.Click += (_, _) => kanban.CancelGoogle();
            root.Children.Add(cancel);
            return root;
        }

        root.Children.Add(Text("Войди один раз тем же аккаунтом, что на сайте канбана — дальше оболочка помнит вход."));
        var google = Accent("Войти через Google");
        google.HorizontalAlignment = HorizontalAlignment.Stretch;
        google.Click += (_, _) => kanban.SignInGoogle();
        root.Children.Add(google);
        if (kanban.SignInError is { } err)
        {
            var e = Text(err);
            e.Foreground = Theme.Brush(Theme.Warning);
            root.Children.Add(e);
        }

        root.Children.Add(Text("или почтой и паролем", 12));
        var email = new TextBox { PlaceholderText = "Почта", FontFamily = Theme.Font, Text = kanban.Api.Email ?? "" };
        var password = new PasswordBox { PlaceholderText = "Пароль" };
        var status = Text("", 12);
        status.Foreground = Theme.Brush(Theme.Warning);
        status.Visibility = Visibility.Collapsed;
        var go = new Button { Content = "Войти", HorizontalAlignment = HorizontalAlignment.Stretch };
        async void Submit()
        {
            go.IsEnabled = false;
            status.Visibility = Visibility.Collapsed;
            var error = await kanban.SignInPassword(email.Text, password.Password);
            go.IsEnabled = true;
            if (error is null) return;
            status.Text = error;
            status.Visibility = Visibility.Visible;
        }
        go.Click += (_, _) => Submit();
        password.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Submit(); } };
        root.Children.Add(email);
        root.Children.Add(password);
        root.Children.Add(go);
        root.Children.Add(status);
        return root;
    }

    /// Голубая кнопка в цвет оболочки (#4cc2ff), а не в цвет акцента Windows.
    public static Button Accent(string text)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var dark = Windows.UI.Color.FromArgb(255, 0x06, 0x23, 0x3A);
        b.Resources["AccentButtonBackground"] = Theme.Brush(Theme.Accent);
        b.Resources["AccentButtonBackgroundPointerOver"] = Theme.Brush(Windows.UI.Color.FromArgb(0xE6, 0x4C, 0xC2, 0xFF));
        b.Resources["AccentButtonBackgroundPressed"] = Theme.Brush(Windows.UI.Color.FromArgb(0xCC, 0x4C, 0xC2, 0xFF));
        b.Resources["AccentButtonForeground"] = Theme.Brush(dark);
        b.Resources["AccentButtonForegroundPointerOver"] = Theme.Brush(dark);
        b.Resources["AccentButtonForegroundPressed"] = Theme.Brush(dark);
        return b;
    }
}
