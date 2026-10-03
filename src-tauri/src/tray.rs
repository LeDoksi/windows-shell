//! Наш значок в трее с меню (до окна настроек): «Стекло» / «Тонировка», закрепление панели и дока,
//! монитор полос, «Перезапустить», «Выход».

use tauri::menu::{CheckMenuItem, IsMenuItem, Menu, MenuItem, PredefinedMenuItem, Submenu};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Listener, Manager, Wry};

use crate::config::{self, Config, ConfigStore, GlassMode};

const MONITOR_ID: &str = "monitor:";

pub fn create(app: &AppHandle) -> tauri::Result<()> {
    let mut tray = TrayIconBuilder::with_id("main")
        .tooltip("Windows Shell")
        .menu(&menu(app, &current(app))?)
        .show_menu_on_left_click(true)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "glass-acrylic" => change(app, |c| c.appearance.glass = GlassMode::Acrylic),
            "glass-tint" => change(app, |c| c.appearance.glass = GlassMode::Tint),
            "pin-launcher" => change(app, |c| c.bars.launcher_pinned ^= true),
            "pin-dock" => change(app, |c| c.bars.dock_pinned ^= true),
            "restart" => {
                // `restart` не доходит до RunEvent::Exit — снимаем AppBar сами.
                #[cfg(windows)]
                crate::bars::shutdown(app);
                app.restart()
            }
            "quit" => app.exit(0),
            id => {
                if let Some(name) = id.strip_prefix(MONITOR_ID) {
                    let name = name.to_string();
                    change(app, |c| c.bars.monitor = Some(name));
                }
            }
        });
    if let Some(icon) = app.default_window_icon() {
        tray = tray.icon(icon.clone());
    }
    tray.build(app)?;

    // Меню собираем заново, когда меняется конфиг (откуда угодно: меню, позже окно настроек)
    // или набор мониторов: так галочки и список всегда фактические.
    for event in ["config_changed", "monitors_changed"] {
        let handle = app.clone();
        app.listen_any(event, move |_| {
            let rebuilt = menu(&handle, &current(&handle));
            if let (Some(tray), Ok(m)) = (handle.tray_by_id("main"), rebuilt) {
                let _ = tray.set_menu(Some(m));
            }
        });
    }
    Ok(())
}

fn menu(app: &AppHandle, config: &Config) -> tauri::Result<Menu<Wry>> {
    let check = |id: &str, text: &str, on: bool| {
        CheckMenuItem::with_id(app, id, text, true, on, None::<&str>)
    };
    let glass = config.appearance.glass;

    // Мониторы как их видит Tauri: имя — то же имя устройства, что в конфиге и в `bars`.
    let monitors = app.available_monitors().unwrap_or_default();
    let primary = app
        .primary_monitor()
        .ok()
        .flatten()
        .map(|m| m.name().cloned());
    let chosen = config
        .bars
        .monitor
        .clone()
        .filter(|c| monitors.iter().any(|m| m.name() == Some(c)))
        .or(primary.clone().flatten());
    let mut screens = Vec::new();
    for (i, m) in monitors.iter().enumerate() {
        let name = m.name().cloned().unwrap_or_default();
        let size = m.size();
        let mut text = format!(
            "Монитор {} — {}×{}, {} %",
            i + 1,
            size.width,
            size.height,
            (m.scale_factor() * 100.0).round()
        );
        if primary.as_ref() == Some(&Some(name.clone())) {
            text.push_str(" (основной)");
        }
        screens.push(check(
            &format!("{MONITOR_ID}{name}"),
            &text,
            chosen.as_deref() == Some(name.as_str()),
        )?);
    }
    let screens: Vec<&dyn IsMenuItem<Wry>> =
        screens.iter().map(|s| s as &dyn IsMenuItem<Wry>).collect();

    Menu::with_items(
        app,
        &[
            &check("glass-acrylic", "Стекло", glass == GlassMode::Acrylic)?,
            &check("glass-tint", "Тонировка", glass == GlassMode::Tint)?,
            &PredefinedMenuItem::separator(app)?,
            &check(
                "pin-launcher",
                "Закрепить панель",
                config.bars.launcher_pinned,
            )?,
            &check("pin-dock", "Закрепить док", config.bars.dock_pinned)?,
            &Submenu::with_items(app, "Монитор", true, &screens)?,
            &PredefinedMenuItem::separator(app)?,
            &MenuItem::with_id(app, "restart", "Перезапустить", true, None::<&str>)?,
            &MenuItem::with_id(app, "quit", "Выход", true, None::<&str>)?,
        ],
    )
}

fn current(app: &AppHandle) -> Config {
    app.state::<ConfigStore>()
        .0
        .lock()
        .expect("config lock")
        .clone()
}

fn change(app: &AppHandle, edit: impl FnOnce(&mut Config)) {
    let mut config = current(app);
    edit(&mut config);
    if let Err(e) = config::set(app, &app.state::<ConfigStore>(), config) {
        eprintln!("[tray] {e}");
    }
}
