//! Глобальные хоткеи звука (этап 5: WSH-33): профили («Наушники 40 %») и «следующее
//! устройство вывода». Сочетания задаются в настройках аудио-виджета строкой `Ctrl+Alt+1`.
//! Свой поток держит `RegisterHotKey`; поменялся конфиг — перерегистрирует всё заново.

use serde::Deserialize;

use crate::config::WidgetEntry;
use crate::widgets::enabled;

pub const MOD_ALT: u32 = 0x1;
pub const MOD_CONTROL: u32 = 0x2;
pub const MOD_SHIFT: u32 = 0x4;
pub const MOD_WIN: u32 = 0x8;

/// `Ctrl+Alt+F1` → модификаторы и виртуальная клавиша Windows. Без модификатора — только F1…F24:
/// иначе хоткей отнимет у всех программ обычную клавишу.
pub fn parse(text: &str) -> Option<(u32, u32)> {
    let parts: Vec<String> = text.split('+').map(|p| p.trim().to_lowercase()).collect();
    let (key, mods) = parts.split_last()?;
    let mut m = 0;
    for part in mods {
        m |= match part.as_str() {
            "ctrl" | "control" => MOD_CONTROL,
            "alt" => MOD_ALT,
            "shift" => MOD_SHIFT,
            "win" => MOD_WIN,
            _ => return None,
        };
    }
    let vk = match key.as_str() {
        k if k.len() == 1 => {
            let c = k.chars().next()?.to_ascii_uppercase();
            c.is_ascii_alphanumeric().then_some(c as u32)?
        }
        k if k.starts_with('f') && k.len() > 1 => {
            let n: u32 = k[1..].parse().ok()?;
            (1..=24).contains(&n).then_some(0x70 + n - 1)?
        }
        "space" => 0x20,
        "pageup" => 0x21,
        "pagedown" => 0x22,
        "end" => 0x23,
        "home" => 0x24,
        "left" => 0x25,
        "up" => 0x26,
        "right" => 0x27,
        "down" => 0x28,
        "insert" => 0x2D,
        "delete" => 0x2E,
        _ => return None,
    };
    let f_key = (0x70..=0x87).contains(&vk);
    (m != 0 || f_key).then_some((m, vk))
}

#[derive(Debug, Clone, PartialEq)]
pub enum Action {
    NextOutput,
    Profile { device_id: String, volume: u32 },
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Profile {
    device_id: String,
    volume: u32,
    hotkey: Option<String>,
}

/// Сочетания из настроек аудио-виджета. Выключен виджет — хоткеев нет.
/// Повтор сочетания — работает первое.
pub fn bindings(widgets: &[WidgetEntry]) -> Vec<((u32, u32), Action)> {
    let Some(audio) = widgets.iter().find(|w| w.id == "audio") else {
        return Vec::new();
    };
    if !enabled(widgets, "audio") {
        return Vec::new();
    }
    let mut out: Vec<((u32, u32), Action)> = Vec::new();
    let mut push = |hotkey: Option<&str>, action: Action| {
        if let Some(key) = hotkey.and_then(parse) {
            if out.iter().all(|(k, _)| *k != key) {
                out.push((key, action));
            }
        }
    };
    push(
        audio
            .settings
            .get("nextOutputHotkey")
            .and_then(|v| v.as_str()),
        Action::NextOutput,
    );
    let profiles: Vec<Profile> = audio
        .settings
        .get("profiles")
        .and_then(|v| serde_json::from_value(v.clone()).ok())
        .unwrap_or_default();
    for p in profiles {
        push(
            p.hotkey.as_deref(),
            Action::Profile {
                device_id: p.device_id,
                volume: p.volume,
            },
        );
    }
    out
}

/// Запустить поток хоткеев (только Windows).
pub fn start(app: &tauri::AppHandle) {
    #[cfg(windows)]
    os::start(app);
    #[cfg(not(windows))]
    let _ = app;
}

#[cfg(windows)]
mod os {
    use std::ptr::null_mut;
    use std::sync::atomic::{AtomicU32, Ordering};

    use tauri::{AppHandle, Listener, Manager};
    use windows_sys::Win32::System::Threading::GetCurrentThreadId;
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        RegisterHotKey, UnregisterHotKey, MOD_NOREPEAT,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        GetMessageW, PostThreadMessageW, MSG, WM_APP, WM_HOTKEY,
    };

    use super::{bindings, Action};
    use crate::config::{Config, ConfigStore, WidgetEntry};

    const WM_RELOAD: u32 = WM_APP + 1;
    static THREAD: AtomicU32 = AtomicU32::new(0);
    static PENDING: std::sync::Mutex<Option<Vec<WidgetEntry>>> = std::sync::Mutex::new(None);

    pub fn start(app: &AppHandle) {
        let widgets = app
            .state::<ConfigStore>()
            .0
            .lock()
            .expect("config lock")
            .widgets
            .clone();
        *PENDING.lock().expect("hotkeys") = Some(widgets);
        app.listen_any("config_changed", |event| {
            if let Ok(config) = serde_json::from_str::<Config>(event.payload()) {
                *PENDING.lock().expect("hotkeys") = Some(config.widgets);
                let t = THREAD.load(Ordering::Relaxed);
                if t != 0 {
                    unsafe { PostThreadMessageW(t, WM_RELOAD, 0, 0) };
                }
            }
        });
        std::thread::spawn(run);
    }

    fn register(widgets: &[WidgetEntry], count: &mut i32) -> Vec<Action> {
        for id in 0..*count {
            unsafe { UnregisterHotKey(null_mut(), id) };
        }
        let list = bindings(widgets);
        let mut actions = Vec::new();
        for ((mods, vk), action) in list {
            let id = actions.len() as i32;
            if unsafe { RegisterHotKey(null_mut(), id, mods | MOD_NOREPEAT, vk) } == 0 {
                // Сочетание уже занято другой программой: молча пропускаем.
                eprintln!(
                    "[hotkeys] занято {mods:#x}+{vk:#x}: {}",
                    std::io::Error::last_os_error()
                );
            }
            actions.push(action);
        }
        *count = actions.len() as i32;
        actions
    }

    fn run() {
        // Сначала очередь сообщений потока, потом его id: иначе первый WM_RELOAD потеряется.
        let mut msg = MSG::default();
        unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::PeekMessageW(&mut msg, null_mut(), 0, 0, 0)
        };
        THREAD.store(unsafe { GetCurrentThreadId() }, Ordering::Relaxed);
        let mut count = 0;
        let mut actions = Vec::new();
        let mut reload = true;
        loop {
            if reload {
                if let Some(widgets) = PENDING.lock().expect("hotkeys").take() {
                    actions = register(&widgets, &mut count);
                }
            }
            if unsafe { GetMessageW(&mut msg, null_mut(), 0, 0) } <= 0 {
                break;
            }
            reload = msg.message == WM_RELOAD;
            if msg.message != WM_HOTKEY {
                continue;
            }
            let result = match actions.get(msg.wParam) {
                Some(Action::NextOutput) => crate::audio::next_output(),
                Some(Action::Profile { device_id, volume }) => {
                    crate::audio::apply_profile(device_id, *volume)
                }
                None => Ok(()),
            };
            if let Err(e) = result {
                eprintln!("[hotkeys] {e}");
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn parses_combinations() {
        assert_eq!(
            parse("Ctrl+Alt+1"),
            Some((MOD_CONTROL | MOD_ALT, '1' as u32))
        );
        assert_eq!(
            parse("shift + win + k"),
            Some((MOD_SHIFT | MOD_WIN, 'K' as u32))
        );
        assert_eq!(parse("F9"), Some((0, 0x78)));
        assert_eq!(parse("Ctrl+Down"), Some((MOD_CONTROL, 0x28)));
        assert_eq!(parse("K"), None, "plain key needs a modifier");
        assert_eq!(parse("Ctrl+"), None);
        assert_eq!(parse("Hyper+K"), None);
        assert_eq!(parse("Ctrl+F25"), None);
        assert_eq!(parse(""), None);
    }

    fn audio(enabled: bool, settings: serde_json::Value) -> Vec<WidgetEntry> {
        vec![WidgetEntry {
            id: "audio".into(),
            enabled,
            settings: settings.as_object().unwrap().clone(),
        }]
    }

    #[test]
    fn bindings_come_from_audio_settings() {
        let settings = json!({
            "nextOutputHotkey": "Ctrl+Alt+0",
            "profiles": [
                { "id": "a", "name": "Наушники", "deviceId": "dev-a", "volume": 40, "hotkey": "Ctrl+Alt+1" },
                { "id": "b", "name": "Колонки", "deviceId": "dev-b", "volume": 70, "hotkey": null },
                { "id": "c", "name": "Дубль", "deviceId": "dev-c", "volume": 10, "hotkey": "ctrl+alt+1" }
            ]
        });
        let list = bindings(&audio(true, settings.clone()));
        assert_eq!(
            list,
            vec![
                ((MOD_CONTROL | MOD_ALT, '0' as u32), Action::NextOutput),
                (
                    (MOD_CONTROL | MOD_ALT, '1' as u32),
                    Action::Profile {
                        device_id: "dev-a".into(),
                        volume: 40
                    }
                ),
            ]
        );
        assert!(bindings(&audio(false, settings)).is_empty());
        assert!(bindings(&audio(true, json!({}))).is_empty());
    }
}
