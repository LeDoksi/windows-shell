//! Конфиг оболочки, схема v1. Rust — источник правды: окна читают конфиг
//! через `config_get`, меняют через `config_set` и узнают об изменениях
//! из события `config_changed`. Каждое принятое изменение сразу пишется на диск
//! (`config.json` в папке настроек приложения, см. [`ConfigFile`]).

use std::collections::HashSet;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
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
    /// Монитор полос — имя устройства Windows (`\\.\DISPLAY2`); `None` или не подключён — основной.
    #[serde(default)]
    pub monitor: Option<String>,
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

/// Миграции схемы: `MIGRATIONS[n - 1]` переводит JSON версии `n` в версию `n + 1`.
/// Новая версия схемы = новая функция здесь + `SCHEMA_VERSION` на единицу больше.
type Migration = fn(&mut serde_json::Value);
const MIGRATIONS: &[Migration] = &[];

/// Разобрать JSON с диска: поднять старую схему до текущей и проверить.
pub fn parse(text: &str) -> Result<Config, String> {
    let mut json: serde_json::Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
    let version = json
        .get("schemaVersion")
        .and_then(serde_json::Value::as_u64)
        .ok_or("нет schemaVersion")?;
    if version == 0 || version > u64::from(SCHEMA_VERSION) {
        return Err(format!("неизвестная версия схемы: {version}"));
    }
    for migrate in &MIGRATIONS[version as usize - 1..] {
        migrate(&mut json);
    }
    json["schemaVersion"] = SCHEMA_VERSION.into();
    let config: Config = serde_json::from_value(json).map_err(|e| e.to_string())?;
    config.validate()?;
    Ok(config)
}

/// Файл конфига на диске. Рядом лежат `config.json.bak` (версия до последней записи)
/// и, если файл оказался испорчен, `config.broken.json` — его не удаляем, а откладываем.
pub struct ConfigFile {
    path: PathBuf,
}

/// Откуда взялся конфиг при запуске.
#[derive(Debug, PartialEq)]
pub enum Loaded {
    /// Первый запуск: дефолты, сразу записаны на диск.
    Fresh,
    File,
    /// Основной файл не читается — взяли бэкап.
    Backup,
    /// Не читается ни файл, ни бэкап: дефолты, испорченный файл отложен.
    Reset,
}

impl ConfigFile {
    pub fn new(dir: &Path) -> Self {
        Self {
            path: dir.join("config.json"),
        }
    }

    fn sibling(&self, suffix: &str) -> PathBuf {
        let mut name = self.path.file_name().unwrap_or_default().to_os_string();
        name.push(suffix);
        self.path.with_file_name(name)
    }

    fn backup(&self) -> PathBuf {
        self.sibling(".bak")
    }

    fn broken(&self) -> PathBuf {
        self.path.with_file_name("config.broken.json")
    }

    /// Прочитать конфиг; что бы ни случилось, вернуть рабочий.
    pub fn load(&self) -> (Config, Loaded) {
        let read = |path: &Path| match fs::read_to_string(path) {
            Ok(text) => Some(parse(&text).map_err(|e| (path.to_owned(), e))),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
            Err(e) => Some(Err((path.to_owned(), e.to_string()))),
        };
        let main = match read(&self.path) {
            Some(Ok(config)) => return (config, Loaded::File),
            Some(Err((path, e))) => {
                eprintln!("[config] {}: {e}", path.display());
                true
            }
            None => false,
        };
        let backup = match read(&self.backup()) {
            Some(Ok(config)) => Some(config),
            Some(Err((path, e))) => {
                eprintln!("[config] {}: {e}", path.display());
                None
            }
            None => None,
        };
        if !main && backup.is_none() {
            let config = Config::defaults();
            self.report(self.save(&config));
            return (config, Loaded::Fresh);
        }
        // Испорченный файл откладываем, а не перезаписываем: вдруг его можно спасти руками.
        if main {
            if let Err(e) = fs::rename(&self.path, self.broken()) {
                eprintln!("[config] не удалось отложить испорченный файл: {e}");
            }
        }
        let (config, loaded) = match backup {
            Some(config) => (config, Loaded::Backup),
            None => (Config::defaults(), Loaded::Reset),
        };
        self.report(self.save(&config));
        (config, loaded)
    }

    fn report(&self, result: Result<(), String>) {
        if let Err(e) = result {
            eprintln!("[config] {e}");
        }
    }

    /// Атомарная запись: новый текст во временный файл, текущий файл — в бэкап,
    /// временный — на место основного. Оборвись запись посередине, на диске
    /// останется либо старый файл, либо новый, но не половина.
    pub fn save(&self, config: &Config) -> Result<(), String> {
        let fail = |what: &str, e: std::io::Error| format!("{what} {}: {e}", self.path.display());
        if let Some(dir) = self.path.parent() {
            fs::create_dir_all(dir).map_err(|e| fail("не создать папку для", e))?;
        }
        let text = serde_json::to_string_pretty(config).map_err(|e| e.to_string())?;
        let tmp = self.sibling(".tmp");
        let mut file = fs::File::create(&tmp).map_err(|e| fail("не записать", e))?;
        file.write_all(text.as_bytes())
            .and_then(|()| file.sync_all())
            .map_err(|e| fail("не записать", e))?;
        drop(file);
        if self.path.exists() {
            fs::copy(&self.path, self.backup()).map_err(|e| fail("не сделать бэкап", e))?;
        }
        fs::rename(&tmp, &self.path).map_err(|e| fail("не записать", e))
    }
}

/// Конфиг в памяти и, если есть, его файл. Без файла (спайки) живёт только в памяти.
pub struct ConfigStore(pub Mutex<Config>, Option<ConfigFile>);

impl ConfigStore {
    #[cfg(all(windows, feature = "spike"))]
    pub fn in_memory() -> Self {
        Self(Mutex::new(Config::defaults()), None)
    }

    pub fn load(dir: &Path) -> Self {
        let file = ConfigFile::new(dir);
        let (config, loaded) = file.load();
        if loaded != Loaded::File {
            eprintln!("[config] {loaded:?}: {}", file.path.display());
        }
        Self(Mutex::new(config), Some(file))
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

/// Проверить, записать на диск и разослать `config_changed` всем окнам (и слушателям в Rust).
/// Не записалось — конфиг остаётся прежним.
pub fn set(app: &AppHandle, store: &ConfigStore, config: Config) -> Result<(), String> {
    config.validate()?;
    let mut current = store.0.lock().expect("config lock");
    if let Some(file) = &store.1 {
        file.save(&config)?;
    }
    *current = config.clone();
    drop(current);
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

    /// Своя пустая папка на тест: тесты идут параллельно.
    fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("ws-config-{}-{name}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn migrations_cover_every_old_version() {
        assert_eq!(MIGRATIONS.len() as u32, SCHEMA_VERSION - 1);
    }

    #[test]
    fn parse_rejects_future_and_missing_versions() {
        assert!(parse(r#"{"schemaVersion": 99}"#)
            .unwrap_err()
            .contains("99"));
        assert!(parse("{}").is_err());
        assert!(parse("не json").is_err());
        assert_eq!(parse(DEFAULT_CONFIG), Ok(Config::defaults()));
    }

    #[test]
    fn first_run_writes_defaults() {
        let dir = temp_dir("fresh");
        let file = ConfigFile::new(&dir);
        assert_eq!(file.load(), (Config::defaults(), Loaded::Fresh));
        assert_eq!(
            parse(&fs::read_to_string(dir.join("config.json")).unwrap()),
            Ok(Config::defaults())
        );
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn save_then_load_keeps_changes_and_backs_up_previous() {
        let dir = temp_dir("save");
        let file = ConfigFile::new(&dir);
        let mut config = Config::defaults();
        file.save(&config).unwrap();
        config.appearance.glass = GlassMode::Tint;
        file.save(&config).unwrap();
        assert_eq!(file.load(), (config, Loaded::File));
        let backup = parse(&fs::read_to_string(dir.join("config.json.bak")).unwrap()).unwrap();
        assert_eq!(backup.appearance.glass, GlassMode::Acrylic);
        assert!(!dir.join("config.json.tmp").exists());
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn broken_file_falls_back_to_backup_and_is_kept_aside() {
        let dir = temp_dir("broken");
        let file = ConfigFile::new(&dir);
        let mut config = Config::defaults();
        config.bars.dock_pinned = true;
        file.save(&config).unwrap();
        file.save(&config).unwrap();
        fs::write(dir.join("config.json"), "{ обрезано").unwrap();
        assert_eq!(file.load(), (config.clone(), Loaded::Backup));
        assert_eq!(
            fs::read_to_string(dir.join("config.broken.json")).unwrap(),
            "{ обрезано"
        );
        // Бэкап стал основным файлом.
        assert_eq!(file.load(), (config, Loaded::File));
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn nothing_readable_resets_to_defaults() {
        let dir = temp_dir("reset");
        fs::create_dir_all(&dir).unwrap();
        fs::write(dir.join("config.json"), r#"{"schemaVersion": 99}"#).unwrap();
        let file = ConfigFile::new(&dir);
        assert_eq!(file.load(), (Config::defaults(), Loaded::Reset));
        assert!(dir.join("config.broken.json").exists());
        assert_eq!(file.load(), (Config::defaults(), Loaded::File));
        fs::remove_dir_all(dir).unwrap();
    }

    #[test]
    fn rejects_unknown_schema_version() {
        let mut config = Config::defaults();
        config.schema_version = 2;
        assert!(config.validate().is_err());
    }
}
