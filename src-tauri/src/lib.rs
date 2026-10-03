mod config;
#[cfg(all(windows, feature = "spike"))]
mod spike;
#[cfg(all(windows, feature = "spike"))]
mod spike_autohide;

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
    let builder = tauri::Builder::default().manage(config::ConfigStore::default());

    #[cfg(not(all(windows, feature = "spike")))]
    let builder = builder.invoke_handler(tauri::generate_handler![
        app_info,
        config::config_get,
        config::config_set
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

    builder
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn app_info_reports_crate_version() {
        assert_eq!(app_info().version, env!("CARGO_PKG_VERSION"));
    }
}
