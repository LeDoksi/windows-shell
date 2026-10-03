mod config;
// Продакшн-окна; сборка спайков (`--features spike`) поднимает вместо них свои.
#[cfg(all(windows, not(feature = "spike")))]
mod dock;
#[cfg(not(feature = "spike"))]
mod shell;
#[cfg(all(windows, feature = "spike"))]
mod spike;
#[cfg(all(windows, feature = "spike"))]
mod spike_autohide;
#[cfg(all(windows, feature = "spike"))]
mod spike_taskbar;
#[cfg(all(windows, feature = "spike"))]
mod spike_tray;

use serde::Serialize;

#[derive(Serialize)]
struct AppInfo {
    version: &'static str,
    platform: &'static str,
}

#[tauri::command]
fn app_info() -> AppInfo {
    AppInfo {
        version: env!("CARGO_PKG_VERSION"),
        platform: std::env::consts::OS,
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // Спайк WSH-43: режимы сторожа и возврата панели задач — без запуска окон.
    #[cfg(all(windows, feature = "spike"))]
    if spike_taskbar::cli() {
        return;
    }

    let builder = tauri::Builder::default().manage(config::ConfigStore::default());

    #[cfg(not(all(windows, feature = "spike")))]
    let builder = builder
        .setup(|app| Ok(shell::create(app.handle())?))
        .invoke_handler(tauri::generate_handler![
            app_info,
            config::config_get,
            config::config_set,
            shell::dock_pill
        ]);

    #[cfg(all(windows, feature = "spike"))]
    let builder = builder
        .setup(|app| Ok(spike::setup(app.handle())?))
        .invoke_handler(tauri::generate_handler![
            app_info,
            config::config_get,
            config::config_set,
            spike::spike_pill
        ]);

    #[cfg(not(all(windows, feature = "spike")))]
    builder
        .run(tauri::generate_context!())
        .expect("error while running tauri application");

    #[cfg(all(windows, feature = "spike"))]
    builder
        .build(tauri::generate_context!())
        .expect("error while building tauri application")
        .run(|_, event| {
            if let tauri::RunEvent::Exit = event {
                spike_taskbar::restore();
            }
        });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn app_info_reports_crate_version() {
        assert_eq!(app_info().version, env!("CARGO_PKG_VERSION"));
    }
}
