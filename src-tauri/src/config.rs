//! Конфиг оболочки, схема v1. Rust — источник правды: окна читают конфиг
//! через `config_get`, меняют через `config_set` и узнают об изменениях
//! из события `config_changed`. Запись на диск — этап 3.

use std::collections::HashSet;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use tauri::{AppHandle, Emitter, State};

/// Тот же JSON, что дефолты и моки фронта.
const DEFAULT_CONFIG: &str = include_str!("../../src/shared/config/default-config.json");

pub const SCHEMA_VERSION: u32 = 1;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ItemKind {
    App,
    File,
    Folder,
    Url,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LauncherItem {
    pub id: String,
    pub kind: ItemKind,
    pub title: String,
    pub target: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub meta: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Category {
    pub id: String,
    pub name: String,
    pub color: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_folder: Option<String>,
    pub items: Vec<LauncherItem>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct DockPin {
    pub id: String,
    pub title: String,
    pub target: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Dock {
    pub pinned: Vec<DockPin>,
}

/// «Стекло» — Acrylic от DWM под доком и панелью; «Тонировка» — без размытия.
/// Только ручной выбор: автоматически режим не меняется (решение владельца).
#[derive(Debug, Clone, Copy, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum GlassMode {
    #[default]
    Acrylic,
    Tint,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
pub struct Appearance {
    pub glass: GlassMode,
}

/// Закреплённая полоса видна всегда и резервирует место (AppBar); иначе — автоскрытие.
#[derive(Debug, Clone, PartialEq, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Bars {
    pub launcher_pinned: bool,
    pub dock_pinned: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Config {
    pub schema_version: u32,
    pub categories: Vec<Category>,
    pub dock: Dock,
    /// Нет в старом конфиге — значения по умолчанию.
    #[serde(default)]
    pub appearance: Appearance,
    #[serde(default)]
    pub bars: Bars,
}

impl Config {
    pub fn defaults() -> Self {
        serde_json::from_str(DEFAULT_CONFIG).expect("default-config.json matches schema v1")
    }

    /// Те же проверки, что `validateConfig` во фронте.
    pub fn validate(&self) -> Result<(), String> {
        if self.schema_version != SCHEMA_VERSION {
            return Err(format!("неизвестная версия схемы: {}", self.schema_version));
        }
        let mut ids = HashSet::new();
        let mut unique = |scope: &str, id: &str| {
            if ids.insert(format!("{scope}:{id}")) {
                Ok(())
            } else {
                Err(format!("повторяется id {scope} «{id}»"))
            }
        };
        for category in &self.categories {
            unique("category", &category.id)?;
            for item in &category.items {
                unique("item", &item.id)?;
            }
        }
        for pin in &self.dock.pinned {
            unique("pin", &pin.id)?;
        }
        Ok(())
    }
}

pub struct ConfigStore(pub Mutex<Config>);

impl Default for ConfigStore {
    fn default() -> Self {
        Self(Mutex::new(Config::defaults()))
    }
}

#[tauri::command]
pub fn config_get(store: State<'_, ConfigStore>) -> Config {
    store.0.lock().expect("config lock").clone()
}

#[tauri::command]
pub fn config_set(
    app: AppHandle,
    store: State<'_, ConfigStore>,
    config: Config,
) -> Result<(), String> {
    set(&app, &store, config)
}

/// Проверить, сохранить и разослать `config_changed` всем окнам (и слушателям в Rust).
pub fn set(app: &AppHandle, store: &ConfigStore, config: Config) -> Result<(), String> {
    config.validate()?;
    *store.0.lock().expect("config lock") = config.clone();
    app.emit("config_changed", config)
        .map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_parse_and_validate() {
        let config = Config::defaults();
        assert_eq!(config.schema_version, SCHEMA_VERSION);
        assert!(!config.categories.is_empty());
        assert_eq!(config.validate(), Ok(()));
    }

    #[test]
    fn defaults_round_trip_through_json() {
        let config = Config::defaults();
        let json = serde_json::to_value(&config).unwrap();
        let original: serde_json::Value = serde_json::from_str(DEFAULT_CONFIG).unwrap();
        assert_eq!(json, original);
    }

    #[test]
    fn rejects_duplicate_ids() {
        let mut config = Config::defaults();
        let first = config.categories[0].clone();
        config.categories.push(first);
        assert!(config
            .validate()
            .unwrap_err()
            .contains("повторяется id category"));
    }

    #[test]
    fn appearance_defaults_to_glass_and_reads_tint() {
        let mut json: serde_json::Value = serde_json::from_str(DEFAULT_CONFIG).unwrap();
        json.as_object_mut().unwrap().remove("appearance");
        let old: Config = serde_json::from_value(json.clone()).unwrap();
        assert_eq!(old.appearance.glass, GlassMode::Acrylic);
        json["appearance"] = serde_json::json!({ "glass": "tint" });
        let tint: Config = serde_json::from_value(json).unwrap();
        assert_eq!(tint.appearance.glass, GlassMode::Tint);
    }

    #[test]
    fn bars_default_to_autohide() {
        let mut json: serde_json::Value = serde_json::from_str(DEFAULT_CONFIG).unwrap();
        json.as_object_mut().unwrap().remove("bars");
        let old: Config = serde_json::from_value(json).unwrap();
        assert_eq!(old.bars, Bars::default());
        assert!(!Config::defaults().bars.launcher_pinned);
    }

    #[test]
    fn rejects_unknown_schema_version() {
        let mut config = Config::defaults();
        config.schema_version = 2;
        assert!(config.validate().is_err());
    }
}
