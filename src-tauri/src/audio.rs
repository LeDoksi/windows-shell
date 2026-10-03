//! Аудио-виджет (этап 5: WSH-31…33). Всё через Windows Core Audio, как в EarTrumpet.
//!
//! - Устройства, громкость и mute вывода и микрофона: команды читают и меняют их напрямую,
//!   а сервис (`Service`) слушает Windows (подключили наушники, сменили громкость клавишами,
//!   поменяли устройство в «Параметрах») и рассылает `audio_changed`.
//! - Громкость по приложениям и пиковые уровни (`audio_mixer`) фронт спрашивает сам,
//!   пока открыт flyout: событий об уровнях у Windows нет, только опрос.
//! - Смена устройства по умолчанию — недокументированный `IPolicyConfig` (стабилен с Win7);
//!   он целиком в `os::policy`. Не сработал — команда вернёт ошибку, остальное работает.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use crate::widgets::WidgetService;

#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Device {
    pub id: String,
    pub name: String,
}

/// Вывод или вход (микрофон): устройства, какое по умолчанию, его громкость и mute.
#[derive(Debug, Clone, PartialEq, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Endpoint {
    pub devices: Vec<Device>,
    /// `None` — устройств нет.
    pub default_id: Option<String>,
    /// 0…100.
    pub volume: u32,
    pub muted: bool,
}

#[derive(Debug, Clone, PartialEq, Default, Serialize)]
pub struct AudioState {
    pub output: Endpoint,
    pub input: Endpoint,
}

/// Звук одного приложения: все его сессии на устройстве вывода по умолчанию.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct Session {
    /// Путь к exe строчными буквами или `system` — системные звуки.
    pub id: String,
    pub name: String,
    pub exe: Option<String>,
    pub volume: u32,
    pub muted: bool,
    /// Пиковый уровень сейчас, 0…1.
    pub peak: f32,
}

/// Микшер: уровни устройств и приложения. Фронт опрашивает, пока открыт flyout.
#[derive(Debug, Clone, PartialEq, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Mixer {
    pub output_peak: f32,
    pub input_peak: f32,
    pub sessions: Vec<Session>,
}

#[derive(Debug, Clone, Copy, PartialEq, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Flow {
    Output,
    Input,
}

/// Одна сессия, как её отдала Windows, — до объединения по приложениям.
#[derive(Debug, Clone, PartialEq)]
pub struct RawSession {
    pub key: String,
    pub name: String,
    pub exe: Option<String>,
    pub volume: f32,
    pub muted: bool,
    pub peak: f32,
}

pub const SYSTEM_SESSION: &str = "system";

/// Громкость Windows (0…1) в проценты, как их показывает сама Windows.
pub fn percent(scalar: f32) -> u32 {
    (scalar.clamp(0.0, 1.0) * 100.0).round() as u32
}

pub fn scalar(percent: u32) -> f32 {
    percent.min(100) as f32 / 100.0
}

/// Ключ приложения: у Chrome, Discord и других много сессий, а ползунок один.
pub fn session_key(exe: Option<&str>, system: bool, pid: u32) -> String {
    match (system, exe) {
        (true, _) => SYSTEM_SESSION.into(),
        (false, Some(exe)) => exe.to_lowercase(),
        (false, None) => format!("pid:{pid}"),
    }
}

/// Сессии одного приложения — в одну строку: громкость первой, mute — если выключены все,
/// уровень — самый громкий. Порядок появления, системные звуки — в конце.
pub fn group(raw: Vec<RawSession>) -> Vec<Session> {
    let mut out: Vec<Session> = Vec::new();
    let mut index: HashMap<String, usize> = HashMap::new();
    for s in raw {
        if let Some(&i) = index.get(&s.key) {
            let g = &mut out[i];
            g.muted &= s.muted;
            g.peak = g.peak.max(s.peak);
            continue;
        }
        index.insert(s.key.clone(), out.len());
        out.push(Session {
            id: s.key,
            name: s.name,
            exe: s.exe,
            volume: percent(s.volume),
            muted: s.muted,
            peak: s.peak,
        });
    }
    out.sort_by_key(|s| s.id == SYSTEM_SESSION);
    out
}

/// Следующее устройство по кругу (хоткей «Следующее устройство вывода»).
pub fn next_device<'a>(devices: &'a [Device], current: Option<&str>) -> Option<&'a str> {
    let i = devices.iter().position(|d| Some(d.id.as_str()) == current);
    let next = i.map_or(0, |i| (i + 1) % devices.len().max(1));
    devices.get(next).map(|d| d.id.as_str())
}

/// Имя сессии: своё у приложения → описание exe → имя файла.
pub fn session_name(display: Option<&str>, description: Option<&str>, exe: Option<&str>) -> String {
    // Строки вида `@%SystemRoot%\…dll,-202` — ссылки на ресурсы, а не имена.
    let display = display
        .map(str::trim)
        .filter(|d| !d.is_empty() && !d.starts_with('@'));
    let stem = exe.map(|e| {
        let file = e.rsplit(['\\', '/']).next().unwrap_or(e);
        file.rsplit_once('.').map_or(file, |(stem, _)| stem)
    });
    [display, description.map(str::trim), stem]
        .into_iter()
        .flatten()
        .find(|s| !s.is_empty())
        .unwrap_or("Приложение")
        .to_string()
}

#[cfg(windows)]
pub use os::Mta;

/// Команды могут ждать Windows (COM): не на главном потоке.
async fn blocking<T: Send + 'static>(
    f: impl FnOnce() -> Result<T, String> + Send + 'static,
) -> Result<T, String> {
    tauri::async_runtime::spawn_blocking(f)
        .await
        .map_err(|e| e.to_string())?
}

#[tauri::command]
pub async fn audio_get() -> Result<AudioState, String> {
    blocking(os::state).await
}

#[tauri::command]
pub async fn audio_set_volume(flow: Flow, volume: u32) -> Result<(), String> {
    blocking(move || os::set_volume(flow, volume)).await
}

#[tauri::command]
pub async fn audio_set_mute(flow: Flow, muted: bool) -> Result<(), String> {
    blocking(move || os::set_mute(flow, muted)).await
}

/// Сделать устройство (вывод или вход) устройством по умолчанию для всех ролей.
#[tauri::command]
pub async fn audio_set_default(id: String) -> Result<(), String> {
    blocking(move || os::set_default(&id)).await
}

/// Профиль: устройство по умолчанию и его громкость.
#[tauri::command]
pub async fn audio_apply_profile(device_id: String, volume: u32) -> Result<(), String> {
    blocking(move || apply_profile(&device_id, volume)).await
}

#[tauri::command]
pub async fn audio_mixer() -> Result<Mixer, String> {
    blocking(os::mixer).await
}

/// Громкость или mute приложения (всех его сессий).
#[tauri::command]
pub async fn audio_session_set(
    id: String,
    volume: Option<u32>,
    muted: Option<bool>,
) -> Result<(), String> {
    blocking(move || os::session_set(&id, volume, muted)).await
}

pub fn apply_profile(device_id: &str, volume: u32) -> Result<(), String> {
    os::set_default(device_id)?;
    os::set_device_volume(device_id, volume)
}

/// Хоткей «следующее устройство вывода».
pub fn next_output() -> Result<(), String> {
    let state = os::state()?;
    match next_device(&state.output.devices, state.output.default_id.as_deref()) {
        Some(id) => os::set_default(id),
        None => Ok(()),
    }
}

/// Сервис виджета: слушает Windows, пока виджет включён.
#[derive(Default)]
pub struct Service {
    #[cfg(windows)]
    running: Option<os::Watcher>,
}

impl WidgetService for Service {
    fn id(&self) -> &'static str {
        "audio"
    }

    fn start(&mut self, app: &tauri::AppHandle) {
        #[cfg(windows)]
        if self.running.is_none() {
            self.running = Some(os::Watcher::start(app.clone()));
        }
        #[cfg(not(windows))]
        let _ = app;
    }

    fn stop(&mut self) {
        #[cfg(windows)]
        if let Some(w) = self.running.take() {
            w.stop();
        }
    }
}

#[cfg(windows)]
mod os {
    use std::collections::HashMap;
    use std::ffi::c_void;
    use std::sync::mpsc::{self, RecvTimeoutError, Sender};
    use std::sync::Mutex;
    use std::time::Duration;

    use tauri::{AppHandle, Emitter};
    use windows::core::{implement, interface, Interface, GUID, HRESULT, PCWSTR, PWSTR};
    use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
    use windows::Win32::Foundation::PROPERTYKEY;
    use windows::Win32::Media::Audio::Endpoints::{
        IAudioEndpointVolume, IAudioEndpointVolumeCallback, IAudioEndpointVolumeCallback_Impl,
        IAudioMeterInformation,
    };
    use windows::Win32::Media::Audio::{
        eCapture, eCommunications, eConsole, eMultimedia, eRender, AudioSessionStateExpired,
        EDataFlow, ERole, IAudioSessionControl2, IAudioSessionManager2, IMMDevice,
        IMMDeviceEnumerator, IMMNotificationClient, IMMNotificationClient_Impl, ISimpleAudioVolume,
        MMDeviceEnumerator, AUDIO_VOLUME_NOTIFICATION_DATA, DEVICE_STATE, DEVICE_STATE_ACTIVE,
    };
    use windows::Win32::System::Com::StructuredStorage::{PropVariantClear, PropVariantToBSTR};
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, CoTaskMemFree, CoUninitialize, CLSCTX_ALL,
        COINIT_MULTITHREADED, STGM_READ,
    };

    use super::{
        group, percent, scalar, session_key, session_name, AudioState, Device, Endpoint, Flow,
        Mixer, RawSession, SYSTEM_SESSION,
    };

    fn err(e: windows::core::Error) -> String {
        e.message()
    }

    /// COM в многопоточном режиме: колбэки Windows приходят в своих потоках без цикла сообщений.
    /// Поток уже в однопоточном (`RPC_E_CHANGED_MODE`) — тоже работает, просто без пары.
    pub struct Mta(bool);

    impl Mta {
        pub fn init() -> Self {
            Self(unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) }.is_ok())
        }
    }

    impl Drop for Mta {
        fn drop(&mut self) {
            if self.0 {
                unsafe { CoUninitialize() };
            }
        }
    }

    fn enumerator() -> Result<IMMDeviceEnumerator, String> {
        unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }.map_err(err)
    }

    fn take_string(p: PWSTR) -> String {
        let s = unsafe { p.to_string() }.unwrap_or_default();
        unsafe { CoTaskMemFree(Some(p.0 as *const c_void)) };
        s
    }

    fn id_of(d: &IMMDevice) -> Option<String> {
        unsafe { d.GetId() }.ok().map(take_string)
    }

    fn name_of(d: &IMMDevice) -> String {
        let name = unsafe {
            d.OpenPropertyStore(STGM_READ)
                .and_then(|store| store.GetValue(&PKEY_Device_FriendlyName))
                .ok()
                .and_then(|mut v| {
                    let s = PropVariantToBSTR(&v).ok().map(|b| b.to_string());
                    let _ = PropVariantClear(&mut v);
                    s
                })
        };
        name.filter(|s| !s.is_empty())
            .unwrap_or_else(|| "Устройство".into())
    }

    fn data_flow(flow: Flow) -> EDataFlow {
        match flow {
            Flow::Output => eRender,
            Flow::Input => eCapture,
        }
    }

    fn default_device(en: &IMMDeviceEnumerator, flow: Flow) -> Result<IMMDevice, String> {
        unsafe { en.GetDefaultAudioEndpoint(data_flow(flow), eConsole) }.map_err(err)
    }

    fn volume_of(d: &IMMDevice) -> Result<IAudioEndpointVolume, String> {
        unsafe { d.Activate(CLSCTX_ALL, None) }.map_err(err)
    }

    fn endpoint(en: &IMMDeviceEnumerator, flow: Flow) -> Endpoint {
        let mut out = Endpoint::default();
        if let Ok(list) = unsafe { en.EnumAudioEndpoints(data_flow(flow), DEVICE_STATE_ACTIVE) } {
            let count = unsafe { list.GetCount() }.unwrap_or(0);
            for i in 0..count {
                if let Ok(d) = unsafe { list.Item(i) } {
                    if let Some(id) = id_of(&d) {
                        out.devices.push(Device {
                            id,
                            name: name_of(&d),
                        });
                    }
                }
            }
        }
        if let Ok(d) = default_device(en, flow) {
            out.default_id = id_of(&d);
            if let Ok(v) = volume_of(&d) {
                out.volume = unsafe { v.GetMasterVolumeLevelScalar() }.map_or(0, percent);
                out.muted = unsafe { v.GetMute() }.is_ok_and(|m| m.as_bool());
            }
        }
        out
    }

    fn read_state(en: &IMMDeviceEnumerator) -> AudioState {
        AudioState {
            output: endpoint(en, Flow::Output),
            input: endpoint(en, Flow::Input),
        }
    }

    pub fn state() -> Result<AudioState, String> {
        let _com = Mta::init();
        Ok(read_state(&enumerator()?))
    }

    pub fn set_volume(flow: Flow, volume: u32) -> Result<(), String> {
        let _com = Mta::init();
        let v = volume_of(&default_device(&enumerator()?, flow)?)?;
        unsafe {
            // Двигаем громкость — снимаем mute, как ползунок Windows.
            v.SetMute(false, std::ptr::null()).map_err(err)?;
            v.SetMasterVolumeLevelScalar(scalar(volume), std::ptr::null())
                .map_err(err)
        }
    }

    pub fn set_mute(flow: Flow, muted: bool) -> Result<(), String> {
        let _com = Mta::init();
        let v = volume_of(&default_device(&enumerator()?, flow)?)?;
        unsafe { v.SetMute(muted, std::ptr::null()) }.map_err(err)
    }

    pub fn set_device_volume(id: &str, volume: u32) -> Result<(), String> {
        let _com = Mta::init();
        let wide: Vec<u16> = id.encode_utf16().chain(Some(0)).collect();
        let d = unsafe { enumerator()?.GetDevice(PCWSTR(wide.as_ptr())) }.map_err(err)?;
        let v = volume_of(&d)?;
        unsafe {
            v.SetMute(false, std::ptr::null()).map_err(err)?;
            v.SetMasterVolumeLevelScalar(scalar(volume), std::ptr::null())
                .map_err(err)
        }
    }

    pub fn set_default(id: &str) -> Result<(), String> {
        let _com = Mta::init();
        policy::set_default(id)
    }

    /// Недокументированный `IPolicyConfig` (его же используют EarTrumpet и SoundSwitch).
    // Имена методов — как в Windows: так их ищут по документации и чужому коду.
    #[allow(non_snake_case)]
    mod policy {
        use super::*;

        const CLSID_POLICY_CONFIG: GUID = GUID::from_u128(0x870af99c_171d_4f9e_af0d_e63df40c2bc9);

        /// Порядок методов важен: вызывается только `SetDefaultEndpoint`, остальные — места в
        /// таблице.
        #[interface("f8679f50-850a-41cf-9c72-430f290290c8")]
        unsafe trait IPolicyConfig: windows::core::IUnknown {
            fn GetMixFormat(&self, id: PCWSTR, format: *mut *mut c_void) -> HRESULT;
            fn GetDeviceFormat(
                &self,
                id: PCWSTR,
                default: i32,
                format: *mut *mut c_void,
            ) -> HRESULT;
            fn ResetDeviceFormat(&self, id: PCWSTR) -> HRESULT;
            fn SetDeviceFormat(
                &self,
                id: PCWSTR,
                endpoint: *mut c_void,
                mix: *mut c_void,
            ) -> HRESULT;
            fn GetProcessingPeriod(
                &self,
                id: PCWSTR,
                default: i32,
                period: *mut i64,
                min: *mut i64,
            ) -> HRESULT;
            fn SetProcessingPeriod(&self, id: PCWSTR, period: *mut i64) -> HRESULT;
            fn GetShareMode(&self, id: PCWSTR, mode: *mut c_void) -> HRESULT;
            fn SetShareMode(&self, id: PCWSTR, mode: *mut c_void) -> HRESULT;
            fn GetPropertyValue(
                &self,
                id: PCWSTR,
                key: *const PROPERTYKEY,
                value: *mut c_void,
            ) -> HRESULT;
            fn SetPropertyValue(
                &self,
                id: PCWSTR,
                key: *const PROPERTYKEY,
                value: *mut c_void,
            ) -> HRESULT;
            fn SetDefaultEndpoint(&self, id: PCWSTR, role: ERole) -> HRESULT;
            fn SetEndpointVisibility(&self, id: PCWSTR, visible: i32) -> HRESULT;
        }

        pub fn set_default(id: &str) -> Result<(), String> {
            let wide: Vec<u16> = id.encode_utf16().chain(Some(0)).collect();
            let policy: IPolicyConfig =
                unsafe { CoCreateInstance(&CLSID_POLICY_CONFIG, None, CLSCTX_ALL) }
                    .map_err(|e| format!("смена устройства недоступна: {}", e.message()))?;
            // Как «Параметры»: устройство становится основным и для звонков.
            for role in [eConsole, eMultimedia, eCommunications] {
                unsafe { policy.SetDefaultEndpoint(PCWSTR(wide.as_ptr()), role) }
                    .ok()
                    .map_err(err)?;
            }
            Ok(())
        }
    }

    /// Имена приложений по exe: описание из ресурсов читать дорого, а микшер опрашивается часто.
    static NAMES: Mutex<Option<HashMap<String, String>>> = Mutex::new(None);

    fn app_name(display: Option<&str>, exe: Option<&str>) -> String {
        let Some(exe) = exe else {
            return session_name(display, None, None);
        };
        let key = exe.to_lowercase();
        if let Some(name) = NAMES
            .lock()
            .expect("names")
            .as_ref()
            .and_then(|m| m.get(&key))
        {
            return name.clone();
        }
        let description = crate::apps::file_description(exe);
        let name = session_name(display, description.as_deref(), Some(exe));
        NAMES
            .lock()
            .expect("names")
            .get_or_insert_with(HashMap::new)
            .insert(key, name.clone());
        name
    }

    /// Сессии устройства вывода по умолчанию (без закончившихся).
    fn sessions(en: &IMMDeviceEnumerator) -> Result<Vec<(String, IAudioSessionControl2)>, String> {
        let d = default_device(en, Flow::Output)?;
        let manager: IAudioSessionManager2 =
            unsafe { d.Activate(CLSCTX_ALL, None) }.map_err(err)?;
        let list = unsafe { manager.GetSessionEnumerator() }.map_err(err)?;
        let count = unsafe { list.GetCount() }.map_err(err)?;
        let mut out = Vec::new();
        for i in 0..count {
            let Ok(control) = (unsafe { list.GetSession(i) }) else {
                continue;
            };
            let Ok(c2) = control.cast::<IAudioSessionControl2>() else {
                continue;
            };
            if unsafe { c2.GetState() }.is_ok_and(|s| s == AudioSessionStateExpired) {
                continue;
            }
            let system = unsafe { c2.IsSystemSoundsSession() } == windows::Win32::Foundation::S_OK;
            let pid = unsafe { c2.GetProcessId() }.unwrap_or(0);
            let exe = if system || pid == 0 {
                None
            } else {
                crate::apps::process_exe(pid)
            };
            out.push((session_key(exe.as_deref(), system, pid), c2));
        }
        Ok(out)
    }

    fn peak(meter: windows::core::Result<IAudioMeterInformation>) -> f32 {
        meter
            .and_then(|m| unsafe { m.GetPeakValue() })
            .unwrap_or(0.0)
    }

    pub fn mixer() -> Result<Mixer, String> {
        let _com = Mta::init();
        let en = enumerator()?;
        let device_peak = |flow| {
            default_device(&en, flow).map_or(0.0, |d| peak(unsafe { d.Activate(CLSCTX_ALL, None) }))
        };
        let mut raw = Vec::new();
        for (key, c2) in sessions(&en)? {
            let Ok(volume) = c2.cast::<ISimpleAudioVolume>() else {
                continue;
            };
            let exe = (key != SYSTEM_SESSION && !key.starts_with("pid:"))
                .then(|| unsafe { c2.GetProcessId() }.ok())
                .flatten()
                .and_then(crate::apps::process_exe);
            let name = if key == SYSTEM_SESSION {
                "Системные звуки".to_string()
            } else {
                let display = unsafe { c2.GetDisplayName() }.ok().map(take_string);
                app_name(display.as_deref(), exe.as_deref())
            };
            raw.push(RawSession {
                key,
                name,
                exe,
                volume: unsafe { volume.GetMasterVolume() }.unwrap_or(1.0),
                muted: unsafe { volume.GetMute() }.is_ok_and(|m| m.as_bool()),
                peak: peak(c2.cast()),
            });
        }
        Ok(Mixer {
            output_peak: device_peak(Flow::Output),
            input_peak: device_peak(Flow::Input),
            sessions: group(raw),
        })
    }

    pub fn session_set(id: &str, volume: Option<u32>, muted: Option<bool>) -> Result<(), String> {
        let _com = Mta::init();
        for (key, c2) in sessions(&enumerator()?)? {
            if key != id {
                continue;
            }
            let v: ISimpleAudioVolume = c2.cast().map_err(err)?;
            unsafe {
                if let Some(volume) = volume {
                    v.SetMasterVolume(scalar(volume), std::ptr::null())
                        .map_err(err)?;
                }
                if let Some(muted) = muted {
                    v.SetMute(muted, std::ptr::null()).map_err(err)?;
                }
            }
        }
        Ok(())
    }

    enum Msg {
        Changed,
        Stop,
    }

    /// Колбэки Windows только будят поток сервиса: звать аудио-API из них нельзя (взаимоблокировка).
    #[implement(IMMNotificationClient)]
    struct DeviceEvents(Sender<Msg>);

    impl IMMNotificationClient_Impl for DeviceEvents_Impl {
        fn OnDeviceStateChanged(&self, _: &PCWSTR, _: DEVICE_STATE) -> windows::core::Result<()> {
            let _ = self.0.send(Msg::Changed);
            Ok(())
        }
        fn OnDeviceAdded(&self, _: &PCWSTR) -> windows::core::Result<()> {
            let _ = self.0.send(Msg::Changed);
            Ok(())
        }
        fn OnDeviceRemoved(&self, _: &PCWSTR) -> windows::core::Result<()> {
            let _ = self.0.send(Msg::Changed);
            Ok(())
        }
        fn OnDefaultDeviceChanged(
            &self,
            _: EDataFlow,
            _: ERole,
            _: &PCWSTR,
        ) -> windows::core::Result<()> {
            let _ = self.0.send(Msg::Changed);
            Ok(())
        }
        fn OnPropertyValueChanged(
            &self,
            _: &PCWSTR,
            key: &PROPERTYKEY,
        ) -> windows::core::Result<()> {
            // Переименовали устройство; прочие свойства меняются часто и нам не нужны.
            if *key == PKEY_Device_FriendlyName {
                let _ = self.0.send(Msg::Changed);
            }
            Ok(())
        }
    }

    #[implement(IAudioEndpointVolumeCallback)]
    struct VolumeEvents(Sender<Msg>);

    impl IAudioEndpointVolumeCallback_Impl for VolumeEvents_Impl {
        fn OnNotify(&self, _: *mut AUDIO_VOLUME_NOTIFICATION_DATA) -> windows::core::Result<()> {
            let _ = self.0.send(Msg::Changed);
            Ok(())
        }
    }

    /// Пачка событий (громкость крутят колесом) — один пересчёт.
    const DEBOUNCE: Duration = Duration::from_millis(30);

    /// Поток сервиса: подписки на Windows и рассылка `audio_changed`.
    pub struct Watcher(Sender<Msg>);

    impl Watcher {
        pub fn start(app: AppHandle) -> Self {
            let (tx, rx) = mpsc::channel();
            let events = tx.clone();
            std::thread::spawn(move || {
                if let Err(e) = watch(&app, events, &rx) {
                    eprintln!("[audio] {e}");
                }
            });
            Self(tx)
        }

        pub fn stop(self) {
            let _ = self.0.send(Msg::Stop);
        }
    }

    fn watch(app: &AppHandle, tx: Sender<Msg>, rx: &mpsc::Receiver<Msg>) -> Result<(), String> {
        let _com = Mta::init();
        let en = enumerator()?;
        let client: IMMNotificationClient = DeviceEvents(tx.clone()).into();
        unsafe { en.RegisterEndpointNotificationCallback(&client) }.map_err(err)?;
        // Громкость слушаем у устройств по умолчанию; сменились — переподписываемся.
        let mut watched: Vec<(IAudioEndpointVolume, IAudioEndpointVolumeCallback)> = Vec::new();
        let mut defaults: Option<(Option<String>, Option<String>)> = None;
        let mut last: Option<AudioState> = None;
        'outer: loop {
            let state = read_state(&en);
            let now = (
                state.output.default_id.clone(),
                state.input.default_id.clone(),
            );
            if defaults.as_ref() != Some(&now) {
                for (v, cb) in watched.drain(..) {
                    let _ = unsafe { v.UnregisterControlChangeNotify(&cb) };
                }
                for flow in [Flow::Output, Flow::Input] {
                    let Ok(v) = default_device(&en, flow).and_then(|d| volume_of(&d)) else {
                        continue;
                    };
                    let cb: IAudioEndpointVolumeCallback = VolumeEvents(tx.clone()).into();
                    if unsafe { v.RegisterControlChangeNotify(&cb) }.is_ok() {
                        watched.push((v, cb));
                    }
                }
                defaults = Some(now);
            }
            if last.as_ref() != Some(&state) {
                let _ = app.emit("audio_changed", &state);
                last = Some(state);
            }
            match rx.recv() {
                Ok(Msg::Changed) => {}
                Ok(Msg::Stop) | Err(_) => break,
            }
            loop {
                match rx.recv_timeout(DEBOUNCE) {
                    Ok(Msg::Changed) => {}
                    Ok(Msg::Stop) | Err(RecvTimeoutError::Disconnected) => break 'outer,
                    Err(RecvTimeoutError::Timeout) => break,
                }
            }
        }
        for (v, cb) in watched {
            let _ = unsafe { v.UnregisterControlChangeNotify(&cb) };
        }
        let _ = unsafe { en.UnregisterEndpointNotificationCallback(&client) };
        Ok(())
    }
}

/// Без Windows звука нет: браузерный режим работает на моках фронта.
#[cfg(not(windows))]
mod os {
    use super::{AudioState, Flow, Mixer};

    const NO: &str = "звук есть только в Windows";

    pub fn state() -> Result<AudioState, String> {
        Ok(AudioState::default())
    }
    pub fn set_volume(_: Flow, _: u32) -> Result<(), String> {
        Err(NO.into())
    }
    pub fn set_mute(_: Flow, _: bool) -> Result<(), String> {
        Err(NO.into())
    }
    pub fn set_device_volume(_: &str, _: u32) -> Result<(), String> {
        Err(NO.into())
    }
    pub fn set_default(_: &str) -> Result<(), String> {
        Err(NO.into())
    }
    pub fn mixer() -> Result<Mixer, String> {
        Ok(Mixer::default())
    }
    pub fn session_set(_: &str, _: Option<u32>, _: Option<bool>) -> Result<(), String> {
        Err(NO.into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn raw(key: &str, volume: f32, muted: bool, peak: f32) -> RawSession {
        RawSession {
            key: key.into(),
            name: key.into(),
            exe: None,
            volume,
            muted,
            peak,
        }
    }

    #[test]
    fn percent_and_scalar_round_trip() {
        assert_eq!(percent(0.639), 64);
        assert_eq!(percent(1.7), 100);
        assert_eq!(percent(-1.0), 0);
        assert_eq!(scalar(150), 1.0);
        for p in 0..=100 {
            assert_eq!(percent(scalar(p)), p);
        }
    }

    #[test]
    fn session_keys_group_by_exe() {
        assert_eq!(
            session_key(Some("C:\\Chrome.EXE"), false, 7),
            "c:\\chrome.exe"
        );
        assert_eq!(session_key(Some("C:\\x.exe"), true, 0), SYSTEM_SESSION);
        assert_eq!(session_key(None, false, 42), "pid:42");
    }

    #[test]
    fn sessions_of_one_app_become_one_row() {
        let rows = group(vec![
            raw(SYSTEM_SESSION, 1.0, false, 0.0),
            raw("chrome", 0.8, true, 0.1),
            raw("discord", 0.45, false, 0.0),
            raw("chrome", 0.3, false, 0.6),
        ]);
        let ids: Vec<_> = rows.iter().map(|r| r.id.as_str()).collect();
        assert_eq!(ids, ["chrome", "discord", SYSTEM_SESSION]);
        assert_eq!(rows[0].volume, 80);
        assert!(!rows[0].muted, "muted only if every session is muted");
        assert_eq!(rows[0].peak, 0.6);
    }

    #[test]
    fn next_device_cycles() {
        let devices: Vec<Device> = ["a", "b", "c"]
            .iter()
            .map(|id| Device {
                id: (*id).into(),
                name: (*id).into(),
            })
            .collect();
        assert_eq!(next_device(&devices, Some("a")), Some("b"));
        assert_eq!(next_device(&devices, Some("c")), Some("a"));
        assert_eq!(next_device(&devices, None), Some("a"));
        assert_eq!(next_device(&devices, Some("gone")), Some("a"));
        assert_eq!(next_device(&[], None), None);
    }

    #[test]
    fn session_names_skip_resource_strings() {
        assert_eq!(session_name(Some("Spotify"), Some("x"), None), "Spotify");
        assert_eq!(
            session_name(Some("@%SystemRoot%\\a.dll,-1"), Some("Google Chrome"), None),
            "Google Chrome"
        );
        assert_eq!(
            session_name(None, None, Some("C:\\Games\\Steam\\steam.exe")),
            "steam"
        );
        assert_eq!(session_name(Some(" "), None, None), "Приложение");
    }
}
