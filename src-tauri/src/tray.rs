//! Наш значок в трее с меню (до окна настроек): «Стекло» / «Тонировка», закрепление панели и дока,
//! «Перезапустить», «Выход».

use tauri::menu::{CheckMenuItem, Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Listener, Manager};

use crate::config::{self, Config, ConfigStore, GlassMode};

pub fn create(app: &AppHandle) -> tauri::Result<()> {
    let config = current(app);
    let mode = config.appearance.glass;
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
    let pin_launcher = CheckMenuItem::with_id(
        app,
        "pin-launcher",
        "Закрепить панель",
        true,
        config.bars.launcher_pinned,
        None::<&str>,
    )?;
    let pin_dock = CheckMenuItem::with_id(
        app,
        "pin-dock",
        "Закрепить док",
        true,
        config.bars.dock_pinned,
        None::<&str>,
    )?;
    let menu = Menu::with_items(
        app,
        &[
            &acrylic,
            &tint,
            &PredefinedMenuItem::separator(app)?,
            &pin_launcher,
            &pin_dock,
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
            _ => {}
        });
    if let Some(icon) = app.default_window_icon() {
        tray = tray.icon(icon.clone());
    }
    tray.build(app)?;

    // Галочки следуют за конфигом (клик по пункту-галочке сам его переключает), откуда бы он ни поменялся (меню, позже окно настроек).
    app.listen_any("config_changed", move |event| {
        if let Ok(config) = serde_json::from_str::<Config>(event.payload()) {
            let glass = config.appearance.glass;
            let _ = acrylic.set_checked(glass == GlassMode::Acrylic);
            let _ = tint.set_checked(glass == GlassMode::Tint);
            let _ = pin_launcher.set_checked(config.bars.launcher_pinned);
            let _ = pin_dock.set_checked(config.bars.dock_pinned);
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

fn change(app: &AppHandle, edit: impl FnOnce(&mut Config)) {
    let mut config = current(app);
    edit(&mut config);
    if let Err(e) = config::set(app, &app.state::<ConfigStore>(), config) {
        eprintln!("[tray] {e}");
    }
}
