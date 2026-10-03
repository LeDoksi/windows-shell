//! Наш значок в трее с меню (до окна настроек): «Стекло» / «Тонировка», «Перезапустить», «Выход».

use tauri::menu::{CheckMenuItem, Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Listener, Manager, Wry};

use crate::config::{self, Config, ConfigStore, GlassMode};

pub fn create(app: &AppHandle) -> tauri::Result<()> {
    let mode = current(app).appearance.glass;
    let acrylic = CheckMenuItem::with_id(
        app,
        "glass-acrylic",
        "Стекло",
        true,
        mode == GlassMode::Acrylic,
        None::<&str>,
    )?;
    let tint = CheckMenuItem::with_id(
        app,
        "glass-tint",
        "Тонировка",
        true,
        mode == GlassMode::Tint,
        None::<&str>,
    )?;
    let menu = Menu::with_items(
        app,
        &[
            &acrylic,
            &tint,
            &PredefinedMenuItem::separator(app)?,
            &MenuItem::with_id(app, "restart", "Перезапустить", true, None::<&str>)?,
            &MenuItem::with_id(app, "quit", "Выход", true, None::<&str>)?,
        ],
    )?;

    let mut tray = TrayIconBuilder::with_id("main")
        .tooltip("Windows Shell")
        .menu(&menu)
        .show_menu_on_left_click(true)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "glass-acrylic" => set_glass(app, GlassMode::Acrylic),
            "glass-tint" => set_glass(app, GlassMode::Tint),
            "restart" => app.restart(),
            "quit" => app.exit(0),
            _ => {}
        });
    if let Some(icon) = app.default_window_icon() {
        tray = tray.icon(icon.clone());
    }
    tray.build(app)?;

    // Галочки следуют за конфигом, откуда бы он ни поменялся (меню, позже окно настроек).
    let checks = (acrylic, tint);
    app.listen_any("config_changed", move |event| {
        if let Ok(config) = serde_json::from_str::<Config>(event.payload()) {
            sync_checks(&checks, config.appearance.glass);
        }
    });
    Ok(())
}

fn current(app: &AppHandle) -> Config {
    app.state::<ConfigStore>()
        .0
        .lock()
        .expect("config lock")
        .clone()
}

fn set_glass(app: &AppHandle, glass: GlassMode) {
    let mut config = current(app);
    config.appearance.glass = glass;
    if let Err(e) = config::set(app, &app.state::<ConfigStore>(), config) {
        eprintln!("[tray] {e}");
    }
}

/// Клик по пункту-галочке сам переключает его; приводим оба к фактическому режиму.
fn sync_checks((acrylic, tint): &(CheckMenuItem<Wry>, CheckMenuItem<Wry>), mode: GlassMode) {
    let _ = acrylic.set_checked(mode == GlassMode::Acrylic);
    let _ = tint.set_checked(mode == GlassMode::Tint);
}
