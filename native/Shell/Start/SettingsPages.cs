namespace WindowsShell.Start;

/// Страницы «Параметров» Windows для поиска в Пуске: название как в Windows 11 и слова,
/// по которым их ищут.
public static class SettingsPages
{
    static StartEntry S(string title, string uri, string glyph, params string[] words) =>
        new(ResultKind.Setting, title, uri, "Параметры") { Glyph = glyph, Keywords = words };

    /// Страницы окна настроек самой оболочки: `shell-settings:<страница>`.
    public const string ShellPrefix = "shell-settings:";

    static StartEntry Shell(string title, string page, string glyph, params string[] words) =>
        new(ResultKind.Setting, title, ShellPrefix + page, "Настройки Windows Shell") { Glyph = glyph, Keywords = words };

    public static readonly IReadOnlyList<StartEntry> All =
    [
        Shell("Настройки оболочки", "", "\uE713", "оболочка", "windows shell", "shell", "настройки оболочки"),
        Shell("Док и хвост", "dock", "\uE8A9", "док", "dock", "хвост", "трей", "закрепить док", "оболочка"),
        Shell("Пуск: недавние файлы и папки", "start", "\uE8FC", "пуск", "start", "быстрые папки", "оболочка"),
        Shell("Стекло или тонировка", "look", "\uE790", "стекло", "тонировка", "прозрачность", "оболочка"),
        Shell("Виджеты оболочки", "widgets", "\uE8A1", "виджеты", "сейчас играет", "звук", "оболочка"),
        Shell("Снимки и запись экрана", "widgets", "\uE722", "снимок", "скриншот", "screenshot", "запись экрана", "prtsc", "sharex", "оболочка"),
        Shell("Клавиши оболочки", "keys", "\uE765", "клавиши", "сочетания", "win", "оболочка"),
        Shell("Система: автозапуск и панель задач", "system", "\uE770", "автозапуск", "панель задач", "перезапустить оболочку", "оболочка"),
        S("Параметры", "ms-settings:", "", "settings", "настройки"),
        S("Дисплей", "ms-settings:display", "", "экран", "разрешение", "масштаб", "яркость", "монитор", "display"),
        S("Ночной свет", "ms-settings:nightlight", "", "night", "тёплый", "синий"),
        S("Звук", "ms-settings:sound", "", "sound", "громкость", "динамики", "микрофон", "наушники"),
        S("Микшер громкости", "ms-settings:apps-volume", "", "громкость", "приложения", "mixer"),
        S("Уведомления", "ms-settings:notifications", "", "notifications", "не беспокоить"),
        S("Фокусировка", "ms-settings:focus", "", "focus", "концентрация"),
        S("Питание и батарея", "ms-settings:powersleep", "", "питание", "сон", "батарея", "энергия", "power", "sleep"),
        S("Батарея", "ms-settings:batterysaver", "", "экономия", "заряд", "battery"),
        S("Память", "ms-settings:storagesense", "", "диск", "место", "storage", "контроль памяти"),
        S("Многозадачность", "ms-settings:multitasking", "", "snap", "окна", "рабочие столы"),
        S("Активация", "ms-settings:activation", "", "ключ", "лицензия", "activation"),
        S("Устранение неполадок", "ms-settings:troubleshoot", "", "неполадки", "troubleshoot"),
        S("Восстановление", "ms-settings:recovery", "", "сброс", "recovery", "переустановка"),
        S("Буфер обмена", "ms-settings:clipboard", "", "clipboard", "журнал"),
        S("Удалённый рабочий стол", "ms-settings:remotedesktop", "", "rdp", "remote"),
        S("О системе", "ms-settings:about", "", "about", "имя компьютера", "версия", "характеристики", "система"),
        S("Bluetooth и устройства", "ms-settings:bluetooth", "", "bluetooth", "блютуз", "устройства"),
        S("Принтеры и сканеры", "ms-settings:printers", "", "printer", "принтер", "сканер"),
        S("Мышь", "ms-settings:mousetouchpad", "", "mouse", "курсор", "колесо"),
        S("Сенсорная панель", "ms-settings:devices-touchpad", "", "touchpad", "тачпад"),
        S("Автозапуск устройств", "ms-settings:autoplay", "", "autoplay", "флешка"),
        S("Сеть и Интернет", "ms-settings:network", "", "network", "сеть", "интернет", "ethernet"),
        S("Wi-Fi", "ms-settings:network-wifi", "", "wifi", "вайфай", "беспроводная"),
        S("VPN", "ms-settings:network-vpn", "", "vpn", "впн"),
        S("Прокси-сервер", "ms-settings:network-proxy", "", "proxy", "прокси"),
        S("Мобильный хот-спот", "ms-settings:network-mobilehotspot", "", "hotspot", "точка доступа", "раздать"),
        S("Режим «в самолёте»", "ms-settings:network-airplanemode", "", "airplane", "самолет"),
        S("Персонализация", "ms-settings:personalization", "", "personalization", "оформление"),
        S("Фон", "ms-settings:personalization-background", "", "обои", "wallpaper", "фон рабочего стола"),
        S("Цвета", "ms-settings:colors", "", "цвет", "тёмная тема", "светлая тема", "акцент", "dark"),
        S("Темы", "ms-settings:themes", "", "тема", "themes", "значки рабочего стола"),
        S("Экран блокировки", "ms-settings:lockscreen", "", "блокировка", "lock screen"),
        S("Шрифты", "ms-settings:fonts", "", "fonts", "шрифт"),
        S("Панель задач", "ms-settings:taskbar", "", "taskbar", "трей"),
        S("Установленные приложения", "ms-settings:appsfeatures", "", "удалить программу", "программы", "приложения", "uninstall", "apps"),
        S("Приложения по умолчанию", "ms-settings:defaultapps", "", "по умолчанию", "браузер по умолчанию", "default"),
        S("Автозагрузка", "ms-settings:startupapps", "", "startup", "автозапуск"),
        S("Дополнительные компоненты", "ms-settings:optionalfeatures", "", "компоненты", "features"),
        S("Учётные записи", "ms-settings:accounts", "", "аккаунт", "пользователь", "account"),
        S("Ваши данные", "ms-settings:yourinfo", "", "аватар", "фото профиля"),
        S("Варианты входа", "ms-settings:signinoptions", "", "пароль", "pin", "пин-код", "windows hello", "вход"),
        S("Семья и другие пользователи", "ms-settings:otherusers", "", "семья", "пользователи", "добавить пользователя"),
        S("Резервное копирование", "ms-settings:backup", "", "backup", "onedrive"),
        S("Дата и время", "ms-settings:dateandtime", "", "время", "дата", "часовой пояс", "time"),
        S("Язык и регион", "ms-settings:regionlanguage", "", "язык", "регион", "language", "раскладка"),
        S("Ввод", "ms-settings:typing", "", "клавиатура", "автоисправление", "typing"),
        S("Раскладки клавиатуры", "ms-settings:keyboard", "", "раскладка", "клавиатура", "keyboard", "сменить язык"),
        S("Голосовой ввод и речь", "ms-settings:speech", "", "речь", "speech", "голос"),
        S("Игры", "ms-settings:gaming-gamebar", "", "game bar", "игровая панель", "gaming"),
        S("Игровой режим", "ms-settings:gaming-gamemode", "", "game mode", "игры"),
        S("Записи", "ms-settings:gaming-gamedvr", "", "запись экрана", "клипы", "captures"),
        S("Специальные возможности", "ms-settings:easeofaccess", "", "accessibility", "доступность"),
        S("Размер текста", "ms-settings:easeofaccess-display", "", "крупный текст", "размер шрифта"),
        S("Указатель мыши", "ms-settings:easeofaccess-mousepointer", "", "курсор", "указатель"),
        S("Экранная лупа", "ms-settings:easeofaccess-magnifier", "", "magnifier", "увеличение"),
        S("Конфиденциальность и защита", "ms-settings:privacy", "", "privacy", "приватность", "разрешения"),
        S("Безопасность Windows", "windowsdefender:", "", "антивирус", "защитник", "defender", "вирусы"),
        S("Камера: разрешения", "ms-settings:privacy-webcam", "", "камера", "webcam"),
        S("Микрофон: разрешения", "ms-settings:privacy-microphone", "", "микрофон", "microphone"),
        S("Расположение", "ms-settings:privacy-location", "", "геолокация", "location"),
        S("Центр обновления Windows", "ms-settings:windowsupdate", "", "обновления", "update", "обновить windows"),
        S("Журнал обновлений", "ms-settings:windowsupdate-history", "", "обновления", "история"),
        S("Для разработчиков", "ms-settings:developers", "", "developer", "разработчик"),
    ];
}
