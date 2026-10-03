#[cfg(not(feature = "spike"))]
mod apps;
#[cfg(not(feature = "spike"))]
mod audio;
#[cfg(all(windows, not(feature = "spike")))]
mod bars;
// Спайки держат конфиг только в памяти: файл им не нужен.
#[cfg_attr(feature = "spike", allow(dead_code))]
mod config;
// Продакшн-окна; сборка спайков (`--features spike`) поднимает вместо них свои.
#[cfg(all(windows, not(feature = "spike")))]
mod dock;
#[cfg(not(feature = "spike"))]
mod hotkeys;
#[cfg(not(feature = "spike"))]
mod icons;
#[cfg(not(feature = "spike"))]
mod launch;
#[cfg(not(feature = "spike"))]
mod media;
#[cfg(not(feature = "spike"))]
mod pick;
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
#[cfg(not(feature = "spike"))]
mod tray;
#[cfg(not(feature = "spike"))]
mod widgets;

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

    let builder = tauri::Builder::default();

    #[cfg(not(all(windows, feature = "spike")))]
    let builder = builder
        .setup(|app| {
            use tauri::Manager;
            // Раньше окон: они сразу спрашивают конфиг.
            let dir = app.path().app_config_dir()?;
            app.manage(config::ConfigStore::load(&dir));
            app.manage(icons::IconCache::default());
            shell::create(app.handle())?;
            #[cfg(windows)]
            bars::start(app.handle());
            tray::create(app.handle())?;
            apps::start(app.handle());
            widgets::start(app.handle());
            hotkeys::start(app.handle());
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            app_info,
            config::config_get,
            config::config_set,
            launch::launch,
            launch::reveal,
            launch::paths_exist,
            icons::item_icon,
            pick::pick_paths,
            pick::installed_apps,
            shell::glass_rect,
            apps::windows_list,
            apps::window_activate,
            apps::window_minimize,
            apps::window_close,
            apps::app_targets,
            audio::audio_get,
            audio::audio_set_volume,
            audio::audio_set_mute,
            audio::audio_set_default,
            audio::audio_apply_profile,
            audio::audio_mixer,
            audio::audio_session_set,
            media::media_get,
            media::media_control
        ]);

    #[cfg(all(windows, feature = "spike"))]
    let builder = builder
        .manage(config::ConfigStore::in_memory())
        .setup(|app| Ok(spike::setup(app.handle())?))
        .invoke_handler(tauri::generate_handler![
            app_info,
            config::config_get,
            config::config_set,
            spike::spike_pill
        ]);

    #[cfg(not(all(windows, feature = "spike")))]
    builder
        .build(tauri::generate_context!())
        .expect("error while building tauri application")
        .run(|_app, event| {
            // Зарезервированное AppBar место иначе останется у Explorer.
            #[cfg(windows)]
            if let tauri::RunEvent::Exit = event {
                bars::shutdown(_app);
            }
            #[cfg(not(windows))]
            let _ = event;
        });

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
