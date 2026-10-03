//! Каркас виджетов (этап 5: WSH-30). Виджет — модуль в репозитории: интерфейс во фронте
//! (`src/widgets/`), а если ему нужна система — сервис здесь. Сервис работает, только пока
//! виджет включён в настройках: выключили «Звук» — никто не слушает Windows ради него.

use std::sync::Mutex;

use tauri::{AppHandle, Listener, Manager};

use crate::config::{Config, ConfigStore, WidgetEntry};

/// Системная часть виджета. Команды (`#[tauri::command]`) работают и без запущенного сервиса;
/// сервис — то, что слушает Windows и рассылает события (`audio_changed`, `media_changed`).
pub trait WidgetService: Send {
    /// Тот же id, что у виджета во фронте и в конфиге.
    fn id(&self) -> &'static str;
    fn start(&mut self, app: &AppHandle);
    fn stop(&mut self);
}

/// Включён ли виджет. Виджета нет в конфиге (появился в новой версии) — включён, как во фронте.
pub fn enabled(widgets: &[WidgetEntry], id: &str) -> bool {
    widgets
        .iter()
        .find(|w| w.id == id)
        .is_none_or(|w| w.enabled)
}

static SERVICES: Mutex<Vec<Box<dyn WidgetService>>> = Mutex::new(Vec::new());

fn apply(app: &AppHandle, widgets: &[WidgetEntry]) {
    for service in SERVICES.lock().expect("widget services").iter_mut() {
        if enabled(widgets, service.id()) {
            service.start(app);
        } else {
            service.stop();
        }
    }
}

/// Запустить сервисы включённых виджетов и следить за настройками.
pub fn start(app: &AppHandle) {
    *SERVICES.lock().expect("widget services") = vec![
        Box::new(crate::audio::Service::default()),
        Box::new(crate::media::Service::default()),
    ];
    let widgets = app
        .state::<ConfigStore>()
        .0
        .lock()
        .expect("config lock")
        .widgets
        .clone();
    apply(app, &widgets);
    let handle = app.clone();
    app.listen_any("config_changed", move |event| {
        if let Ok(config) = serde_json::from_str::<Config>(event.payload()) {
            apply(&handle, &config.widgets);
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(id: &str, enabled: bool) -> WidgetEntry {
        WidgetEntry {
            id: id.into(),
            enabled,
            settings: Default::default(),
        }
    }

    #[test]
    fn missing_widgets_are_enabled() {
        let widgets = [entry("audio", false), entry("media", true)];
        assert!(!enabled(&widgets, "audio"));
        assert!(enabled(&widgets, "media"));
        assert!(enabled(&widgets, "new"));
    }
}
