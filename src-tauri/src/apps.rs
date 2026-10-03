//! Окна запущенных программ для дока (этап 4: WSH-25…29).
//!
//! Свой поток с невидимым окном слушает Windows: `SetWinEventHook` (окно появилось, исчезло,
//! свернулось, сменился заголовок или активное окно) и shell hook (окно мигает, просит внимания).
//! На каждое событие — короткая пауза и полный пересчёт списка через `EnumWindows`: это дешевле
//! и надёжнее, чем собирать список из отдельных событий. Если список поменялся — событие
//! `windows_changed` всем окнам. Группировку по приложениям и сопоставление с закреплёнными
//! делает фронт (`src/dock/model.ts`).

use std::collections::{HashMap, HashSet};
use std::sync::Mutex;

use serde::Serialize;

use crate::launch::is_path;

/// Окно программы, как его видит док.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowInfo {
    /// `HWND` числом: им фронт просит активировать, свернуть или закрыть окно.
    pub hwnd: i64,
    pub title: String,
    /// AppUserModelID окна или процесса, если есть (UWP, Chrome, Telegram…).
    pub aumid: Option<String>,
    /// Путь к exe процесса. У UWP — процесс самого приложения, а не ApplicationFrameHost.
    pub exe: Option<String>,
    /// Название приложения для подсказки: из «Пуска», из описания exe или имя файла.
    pub app_name: String,
    pub minimized: bool,
    /// Активное окно (или владелец активного диалога). Окна оболочки не в счёт.
    pub active: bool,
    /// Окно мигает: просит внимания.
    pub attention: bool,
    /// Порядок появления: незакреплённые иконки не прыгают по доку.
    pub seq: u64,
    /// Когда окно последний раз было активным (больше — позже); 0 — не было при нас.
    pub focused: u64,
}

/// Закреплённое приложение: есть ли оно в системе и какой exe за ним стоит.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct AppTarget {
    pub exists: bool,
    pub exe: Option<String>,
}

/// Голые факты об окне из Win32 — для фильтра, который проверяется тестами.
#[derive(Debug, Default, Clone)]
pub struct Facts {
    pub visible: bool,
    pub cloaked: bool,
    pub has_owner: bool,
    pub tool: bool,
    pub app_window: bool,
    pub no_activate: bool,
    pub ours: bool,
    pub class: String,
    pub title: String,
}

/// Классы окон самой Windows, которые видимы и без владельца, но в панель задач не попадают.
const SYSTEM_CLASSES: [&str; 5] = [
    "Progman",
    "WorkerW",
    "Shell_TrayWnd",
    "Shell_SecondaryTrayWnd",
    "Windows.UI.Core.CoreWindow",
];

/// Окно попадает в док по тем же правилам, что и в панель задач Windows.
pub fn is_app_window(f: &Facts) -> bool {
    if !f.visible || f.cloaked || f.ours || f.title.trim().is_empty() {
        return false;
    }
    if SYSTEM_CLASSES.contains(&f.class.as_str()) {
        return false;
    }
    // WS_EX_APPWINDOW принудительно показывает окно в панели задач.
    f.app_window || !(f.tool || f.has_owner || f.no_activate)
}

/// Название приложения: «Пуск» → описание exe → имя exe → заголовок окна.
pub fn app_name(
    start_name: Option<&str>,
    description: Option<&str>,
    exe: Option<&str>,
    title: &str,
) -> String {
    // Вручную, а не `Path`: путь Windows, а тесты идут и на других системах.
    let stem = exe.map(|e| {
        let file = e.rsplit(['\\', '/']).next().unwrap_or(e);
        file.rsplit_once('.').map_or(file, |(stem, _)| stem)
    });
    [start_name, description, stem]
        .into_iter()
        .flatten()
        .map(str::trim)
        .find(|s| !s.is_empty())
        .unwrap_or(title)
        .to_string()
}

/// Память трекера между пересчётами: порядок появления, фокус, мигающие окна.
#[derive(Default)]
pub struct Memory {
    next: u64,
    seq: HashMap<i64, u64>,
    focused: HashMap<i64, u64>,
    flashing: HashSet<i64>,
    /// Последнее активное чужое окно (корневой владелец).
    foreground: i64,
}

impl Memory {
    fn tick(&mut self) -> u64 {
        self.next += 1;
        self.next
    }

    /// Активным стало окно `hwnd` (уже приведённое к корневому владельцу).
    pub fn activated(&mut self, hwnd: i64) {
        self.foreground = hwnd;
        self.flashing.remove(&hwnd);
        let t = self.tick();
        self.focused.insert(hwnd, t);
    }

    pub fn flash(&mut self, hwnd: i64) {
        if hwnd != self.foreground {
            self.flashing.insert(hwnd);
        }
    }

    /// Проставить окнам порядок, фокус и внимание; забыть исчезнувшие окна.
    pub fn apply(&mut self, windows: &mut [WindowInfo]) {
        let alive: HashSet<i64> = windows.iter().map(|w| w.hwnd).collect();
        self.seq.retain(|h, _| alive.contains(h));
        self.focused.retain(|h, _| alive.contains(h));
        self.flashing.retain(|h| alive.contains(h));
        for w in windows.iter_mut() {
            w.seq = match self.seq.get(&w.hwnd) {
                Some(&s) => s,
                None => {
                    let s = self.tick();
                    self.seq.insert(w.hwnd, s);
                    s
                }
            };
            w.focused = self.focused.get(&w.hwnd).copied().unwrap_or(0);
            w.active = w.hwnd == self.foreground;
            w.attention = self.flashing.contains(&w.hwnd);
        }
    }
}

/// Последний разосланный список: отдаём окну, которое только что открылось.
static LAST: Mutex<Vec<WindowInfo>> = Mutex::new(Vec::new());

#[tauri::command]
pub fn windows_list() -> Vec<WindowInfo> {
    LAST.lock().expect("windows list").clone()
}

/// Показать окно и сделать активным (свёрнутое — развернуть).
#[tauri::command]
pub fn window_activate(hwnd: i64) -> Result<(), String> {
    os::activate(hwnd)
}

#[tauri::command]
pub fn window_minimize(hwnd: i64) -> Result<(), String> {
    os::minimize(hwnd)
}

/// Попросить окно закрыться, как крестиком: программа может спросить «Сохранить?».
#[tauri::command]
pub fn window_close(hwnd: i64) -> Result<(), String> {
    os::close(hwnd)
}

/// Для закреплённых в доке и плиток-приложений: установлено ли приложение и где его exe.
#[tauri::command]
pub async fn app_targets(targets: Vec<String>) -> Vec<AppTarget> {
    tauri::async_runtime::spawn_blocking(move || {
        #[cfg(windows)]
        let _com = crate::launch::Com::init();
        targets.iter().map(|t| os::target(t.trim())).collect()
    })
    .await
    .unwrap_or_default()
}

/// Запустить трекер окон (только Windows; в других системах окон нет).
pub fn start(app: &tauri::AppHandle) {
    #[cfg(windows)]
    {
        let app = app.clone();
        std::thread::spawn(move || os::run(app));
    }
    #[cfg(not(windows))]
    let _ = app;
}

#[cfg(windows)]
mod os {
    use std::cell::Cell;
    use std::collections::HashMap;
    use std::path::Path;
    use std::ptr::{null, null_mut};
    use std::sync::atomic::{AtomicIsize, AtomicU32, Ordering};
    use std::sync::{Mutex, OnceLock};

    use tauri::{AppHandle, Emitter};
    use windows::core::{GUID, HSTRING};
    use windows::Win32::Storage::EnhancedStorage::{
        PKEY_AppUserModel_ID, PKEY_ItemNameDisplay, PKEY_Link_TargetParsingPath,
    };
    use windows::Win32::System::Com::CoTaskMemFree;
    use windows::Win32::System::Com::StructuredStorage::{PropVariantClear, PropVariantToBSTR};
    use windows::Win32::UI::Shell::PropertiesSystem::{
        IPropertyStore, SHGetPropertyStoreForWindow,
    };
    use windows::Win32::UI::Shell::{
        IShellItem2, SHCreateItemFromParsingName, SHGetKnownFolderPath, KF_FLAG_DEFAULT,
    };
    use windows_sys::Win32::Foundation::{CloseHandle, HWND, LPARAM, LRESULT, WPARAM};
    use windows_sys::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED};
    use windows_sys::Win32::Storage::FileSystem::{
        GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW,
    };
    use windows_sys::Win32::Storage::Packaging::Appx::GetApplicationUserModelId;
    use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows_sys::Win32::System::Threading::{
        AttachThreadInput, GetCurrentThreadId, OpenProcess, QueryFullProcessImageNameW,
        PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
    };
    use windows_sys::Win32::UI::Accessibility::{SetWinEventHook, HWINEVENTHOOK};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        BringWindowToTop, CreateWindowExW, DefWindowProcW, DispatchMessageW, EnumChildWindows,
        EnumWindows, GetAncestor, GetClassNameW, GetForegroundWindow, GetMessageW, GetWindow,
        GetWindowLongPtrW, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId,
        IsIconic, IsWindow, IsWindowVisible, KillTimer, PostMessageW, RegisterClassW,
        RegisterShellHookWindow, RegisterWindowMessageW, SetForegroundWindow, SetTimer,
        ShowWindowAsync, TranslateMessage, CHILDID_SELF, EVENT_OBJECT_CLOAKED, EVENT_OBJECT_CREATE,
        EVENT_OBJECT_HIDE, EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_UNCLOAKED,
        EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZESTART, GA_ROOT,
        GA_ROOTOWNER, GWL_EXSTYLE, GW_OWNER, MSG, OBJID_WINDOW, SW_MINIMIZE, SW_RESTORE,
        WINEVENT_OUTOFCONTEXT, WINEVENT_SKIPOWNPROCESS, WM_APP, WM_CLOSE, WM_TIMER, WNDCLASSW,
        WS_EX_APPWINDOW, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_POPUP,
    };

    use super::{app_name, is_app_window, is_path, AppTarget, Facts, Memory, WindowInfo, LAST};
    use crate::launch::Com;

    /// Пауза перед пересчётом: пачка событий (окно создалось, показалось, сменило заголовок)
    /// превращается в один пересчёт.
    const DEBOUNCE_MS: u32 = 60;
    /// Страховочный пересчёт: заголовки вкладок браузера и всё, что пропустили хуки.
    const SAFETY_MS: u32 = 2000;
    const TIMER_DEBOUNCE: usize = 1;
    const WM_SCHEDULE: u32 = WM_APP + 1;
    const TIMER_SAFETY: usize = 2;

    /// HSHELL_FLASH = HSHELL_REDRAW | HSHELL_HIGHBIT.
    const HSHELL_FLASH: usize = 0x8006;
    const HSHELL_WINDOWACTIVATED: usize = 4;
    const HSHELL_RUDEAPPACTIVATED: usize = 0x8004;

    /// Что успели сообщить хуки до следующего пересчёта.
    enum Event {
        Activated(i64),
        Flash(i64),
    }

    /// Хуки Windows зовутся и посреди COM-вызовов пересчёта, поэтому они только складывают
    /// события сюда (замок держится мгновения) и просят пересчёт.
    static EVENTS: Mutex<Vec<Event>> = Mutex::new(Vec::new());
    /// Невидимое окно трекера: таймеры и shell hook.
    static TRACKER_HWND: AtomicIsize = AtomicIsize::new(0);
    static SHELL_MSG: AtomicU32 = AtomicU32::new(0);

    /// Состояние пересчёта; трогает его только поток трекера.
    struct Tracker {
        app: AppHandle,
        memory: Memory,
        /// Названия приложений по ключу (AUMID или путь exe).
        names: HashMap<String, String>,
    }

    static TRACKER: OnceLock<Mutex<Tracker>> = OnceLock::new();

    thread_local! {
        /// COM внутри пересчёта может прокачать сообщения и снова вызвать таймер.
        static RESCANNING: Cell<bool> = const { Cell::new(false) };
    }

    fn push(event: Event) {
        EVENTS.lock().expect("events").push(event);
        schedule();
    }

    fn wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(Some(0)).collect()
    }

    fn from_wide(buf: &[u16]) -> String {
        let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        String::from_utf16_lossy(&buf[..end])
    }

    fn is_ours(h: HWND) -> bool {
        pid_of(h) == std::process::id()
    }

    fn pid_of(h: HWND) -> u32 {
        let mut pid = 0;
        unsafe { GetWindowThreadProcessId(h, &mut pid) };
        pid
    }

    fn root_owner(h: HWND) -> HWND {
        let root = unsafe { GetAncestor(h, GA_ROOTOWNER) };
        if root.is_null() {
            h
        } else {
            root
        }
    }

    pub fn run(app: AppHandle) {
        let _com = Com::init();
        let class = wide("WindowsShellAppsTracker");
        let window = unsafe {
            let instance = GetModuleHandleW(null());
            let wc = WNDCLASSW {
                lpfnWndProc: Some(wndproc),
                hInstance: instance,
                lpszClassName: class.as_ptr(),
                ..Default::default()
            };
            RegisterClassW(&wc);
            // Обычное (не message-only) окно: shell hook приходит только таким. Никогда не показываем.
            CreateWindowExW(
                WS_EX_TOOLWINDOW,
                class.as_ptr(),
                class.as_ptr(),
                WS_POPUP,
                0,
                0,
                0,
                0,
                null_mut(),
                null_mut(),
                instance,
                null(),
            )
        };
        if window.is_null() {
            eprintln!(
                "[apps] no tracker window: {}",
                std::io::Error::last_os_error()
            );
            return;
        }
        TRACKER_HWND.store(window as isize, Ordering::Relaxed);
        SHELL_MSG.store(
            unsafe { RegisterWindowMessageW(wide("SHELLHOOK").as_ptr()) },
            Ordering::Relaxed,
        );
        let mut memory = Memory::default();
        let fg = unsafe { GetForegroundWindow() };
        if !fg.is_null() && !is_ours(fg) {
            memory.activated(root_owner(fg) as i64);
        }
        let _ = TRACKER.set(Mutex::new(Tracker {
            app,
            memory,
            names: HashMap::new(),
        }));
        unsafe {
            if RegisterShellHookWindow(window) == 0 {
                eprintln!("[apps] no shell hook: {}", std::io::Error::last_os_error());
            }
            for (from, to) in [
                (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
                (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                (EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE),
                (EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE),
                (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
            ] {
                SetWinEventHook(
                    from,
                    to,
                    null_mut(),
                    Some(on_event),
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
                );
            }
            SetTimer(window, TIMER_SAFETY, SAFETY_MS, None);
        }
        rescan();
        let mut msg = MSG::default();
        while unsafe { GetMessageW(&mut msg, null_mut(), 0, 0) } > 0 {
            unsafe {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }

    /// Пересчитать чуть позже; новое событие отодвигает пересчёт. Можно звать из любого потока:
    /// таймер ставит сам поток трекера.
    fn schedule() {
        let h = TRACKER_HWND.load(Ordering::Relaxed);
        if h != 0 {
            unsafe { PostMessageW(h as HWND, WM_SCHEDULE, 0, 0) };
        }
    }

    unsafe extern "system" fn on_event(
        _hook: HWINEVENTHOOK,
        event: u32,
        hwnd: HWND,
        object: i32,
        child: i32,
        _thread: u32,
        _time: u32,
    ) {
        if object != OBJID_WINDOW || child != CHILDID_SELF as i32 || hwnd.is_null() {
            return;
        }
        if event == EVENT_SYSTEM_FOREGROUND {
            // Свои окна (клик по доку крадёт фокус на миг) не меняют активное приложение.
            if !is_ours(hwnd) {
                push(Event::Activated(root_owner(hwnd) as i64));
                return;
            }
        } else if GetAncestor(hwnd, GA_ROOT) != hwnd {
            // События летят и от кнопок внутри окон — нам нужны только окна верхнего уровня.
            return;
        }
        schedule();
    }

    unsafe extern "system" fn wndproc(
        h: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        if msg == WM_SCHEDULE {
            SetTimer(h, TIMER_DEBOUNCE, DEBOUNCE_MS, None);
            return 0;
        }
        if msg == WM_TIMER {
            if wparam == TIMER_DEBOUNCE {
                KillTimer(h, TIMER_DEBOUNCE);
            }
            rescan();
            return 0;
        }
        let shell_msg = SHELL_MSG.load(Ordering::Relaxed);
        if shell_msg != 0 && msg == shell_msg {
            match wparam {
                HSHELL_FLASH => push(Event::Flash(lparam as i64)),
                HSHELL_WINDOWACTIVATED | HSHELL_RUDEAPPACTIVATED => schedule(),
                _ => {}
            }
            return 0;
        }
        DefWindowProcW(h, msg, wparam, lparam)
    }

    unsafe extern "system" fn collect(h: HWND, out: LPARAM) -> i32 {
        (*(out as *mut Vec<HWND>)).push(h);
        1
    }

    fn top_level_windows() -> Vec<HWND> {
        let mut all: Vec<HWND> = Vec::new();
        unsafe { EnumWindows(Some(collect), &mut all as *mut _ as LPARAM) };
        all
    }

    fn text(h: HWND) -> String {
        let len = unsafe { GetWindowTextLengthW(h) };
        if len <= 0 {
            return String::new();
        }
        let mut buf = vec![0u16; len as usize + 1];
        let n = unsafe { GetWindowTextW(h, buf.as_mut_ptr(), buf.len() as i32) };
        String::from_utf16_lossy(&buf[..n.max(0) as usize])
    }

    fn class(h: HWND) -> String {
        let mut buf = [0u16; 128];
        let n = unsafe { GetClassNameW(h, buf.as_mut_ptr(), buf.len() as i32) };
        String::from_utf16_lossy(&buf[..n.max(0) as usize])
    }

    fn facts(h: HWND) -> Facts {
        let ex = unsafe { GetWindowLongPtrW(h, GWL_EXSTYLE) } as u32;
        let mut cloaked = 0u32;
        unsafe {
            DwmGetWindowAttribute(h, DWMWA_CLOAKED as u32, &mut cloaked as *mut _ as *mut _, 4)
        };
        Facts {
            visible: unsafe { IsWindowVisible(h) } != 0,
            cloaked: cloaked != 0,
            has_owner: !unsafe { GetWindow(h, GW_OWNER) }.is_null(),
            tool: ex & WS_EX_TOOLWINDOW != 0,
            app_window: ex & WS_EX_APPWINDOW != 0,
            no_activate: ex & WS_EX_NOACTIVATE != 0,
            ours: is_ours(h),
            class: class(h),
            title: text(h),
        }
    }

    /// Процесс по pid: путь к exe и AUMID (у упакованных приложений).
    fn process(pid: u32) -> (Option<String>, Option<String>) {
        unsafe {
            let p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if p.is_null() {
                return (None, None);
            }
            let mut buf = vec![0u16; 1024];
            let mut len = buf.len() as u32;
            let exe =
                (QueryFullProcessImageNameW(p, PROCESS_NAME_WIN32, buf.as_mut_ptr(), &mut len)
                    != 0)
                    .then(|| String::from_utf16_lossy(&buf[..len as usize]));
            let mut len = buf.len() as u32;
            let aumid = (GetApplicationUserModelId(p, &mut len, buf.as_mut_ptr()) == 0)
                .then(|| from_wide(&buf))
                .filter(|s| !s.is_empty());
            CloseHandle(p);
            (exe, aumid)
        }
    }

    /// AUMID, который окну назначила программа (так группирует и панель задач).
    fn window_aumid(h: HWND) -> Option<String> {
        unsafe {
            let store: IPropertyStore =
                SHGetPropertyStoreForWindow(windows::Win32::Foundation::HWND(h)).ok()?;
            let mut value = store.GetValue(&PKEY_AppUserModel_ID).ok()?;
            let text = PropVariantToBSTR(&value).ok().map(|b| b.to_string());
            let _ = PropVariantClear(&mut value);
            text.filter(|s| !s.is_empty())
        }
    }

    unsafe extern "system" fn find_core(h: HWND, out: LPARAM) -> i32 {
        let (frame_pid, found) = &mut *(out as *mut (u32, u32));
        let pid = pid_of(h);
        if pid != *frame_pid && class(h) == "Windows.UI.Core.CoreWindow" {
            *found = pid;
            return 0;
        }
        1
    }

    /// У UWP окно верхнего уровня принадлежит ApplicationFrameHost; само приложение —
    /// в дочернем CoreWindow другого процесса.
    fn uwp_pid(h: HWND, frame_pid: u32) -> Option<u32> {
        let mut state = (frame_pid, 0u32);
        unsafe { EnumChildWindows(h, Some(find_core), &mut state as *mut _ as LPARAM) };
        (state.1 != 0).then_some(state.1)
    }

    /// Описание exe из ресурсов версии («Google Chrome», «Telegram Desktop»).
    fn file_description(exe: &str) -> Option<String> {
        unsafe {
            let path = wide(exe);
            let size = GetFileVersionInfoSizeW(path.as_ptr(), null_mut());
            if size == 0 {
                return None;
            }
            let mut data = vec![0u8; size as usize];
            if GetFileVersionInfoW(path.as_ptr(), 0, size, data.as_mut_ptr().cast()) == 0 {
                return None;
            }
            // Первый язык из таблицы переводов.
            let mut ptr = null_mut();
            let mut len = 0u32;
            let lang = if VerQueryValueW(
                data.as_ptr().cast(),
                wide("\\VarFileInfo\\Translation").as_ptr(),
                &mut ptr,
                &mut len,
            ) != 0
                && len >= 4
            {
                let pair = ptr as *const u16;
                format!("{:04x}{:04x}", *pair, *pair.add(1))
            } else {
                "040904b0".into()
            };
            let key = wide(&format!("\\StringFileInfo\\{lang}\\FileDescription"));
            if VerQueryValueW(data.as_ptr().cast(), key.as_ptr(), &mut ptr, &mut len) == 0
                || len == 0
            {
                return None;
            }
            let text = std::slice::from_raw_parts(ptr as *const u16, len as usize);
            Some(from_wide(text)).filter(|s| !s.trim().is_empty())
        }
    }

    fn shell_item(parsing: &str) -> Option<IShellItem2> {
        unsafe { SHCreateItemFromParsingName(&HSTRING::from(parsing), None).ok() }
    }

    fn item_string(
        item: &IShellItem2,
        key: &windows::Win32::Foundation::PROPERTYKEY,
    ) -> Option<String> {
        unsafe {
            let p = item.GetString(key).ok()?;
            let s = p.to_string().ok();
            CoTaskMemFree(Some(p.0 as *const _));
            s.filter(|s| !s.is_empty())
        }
    }

    fn name_of(t: &mut Tracker, aumid: Option<&str>, exe: Option<&str>, title: &str) -> String {
        let key = aumid.or(exe).unwrap_or(title).to_lowercase();
        if let Some(name) = t.names.get(&key) {
            return name.clone();
        }
        let start = aumid
            .and_then(|a| shell_item(&format!("shell:AppsFolder\\{a}")))
            .and_then(|item| item_string(&item, &PKEY_ItemNameDisplay));
        let description = exe.and_then(file_description);
        let name = app_name(start.as_deref(), description.as_deref(), exe, title);
        t.names.insert(key, name.clone());
        name
    }

    fn rescan() {
        if RESCANNING.with(|r| r.replace(true)) {
            schedule();
            return;
        }
        scan();
        RESCANNING.with(|r| r.set(false));
    }

    fn scan() {
        let Some(tracker) = TRACKER.get() else {
            return;
        };
        let mut windows: Vec<WindowInfo> = Vec::new();
        let mut processes: HashMap<u32, (Option<String>, Option<String>)> = HashMap::new();
        for h in top_level_windows() {
            let f = facts(h);
            if !is_app_window(&f) {
                continue;
            }
            let frame_pid = pid_of(h);
            let pid = if f.class == "ApplicationFrameWindow" {
                uwp_pid(h, frame_pid).unwrap_or(frame_pid)
            } else {
                frame_pid
            };
            let (exe, process_aumid) = processes.entry(pid).or_insert_with(|| process(pid)).clone();
            let aumid = window_aumid(h).or(process_aumid);
            windows.push(WindowInfo {
                hwnd: h as i64,
                title: f.title,
                aumid,
                exe,
                app_name: String::new(),
                minimized: unsafe { IsIconic(h) } != 0,
                active: false,
                attention: false,
                seq: 0,
                focused: 0,
            });
        }
        let app = {
            let mut t = tracker.lock().expect("tracker");
            for w in &mut windows {
                w.app_name = name_of(&mut t, w.aumid.as_deref(), w.exe.as_deref(), &w.title);
            }
            for event in EVENTS.lock().expect("events").drain(..) {
                match event {
                    Event::Activated(h) => t.memory.activated(h),
                    Event::Flash(h) => t.memory.flash(h),
                }
            }
            t.memory.apply(&mut windows);
            t.app.clone()
        };
        let mut last = LAST.lock().expect("windows list");
        if *last != windows {
            *last = windows.clone();
            drop(last);
            let _ = app.emit("windows_changed", windows);
        }
    }

    fn window(hwnd: i64) -> Result<HWND, String> {
        let h = hwnd as HWND;
        if unsafe { IsWindow(h) } == 0 {
            return Err("окно уже закрыто".into());
        }
        Ok(h)
    }

    pub fn activate(hwnd: i64) -> Result<(), String> {
        let h = window(hwnd)?;
        unsafe {
            if IsIconic(h) != 0 {
                ShowWindowAsync(h, SW_RESTORE);
            }
            // Клик по доку — ввод в наш процесс, так что Windows обычно разрешает.
            if SetForegroundWindow(h) == 0 {
                // Запасной путь: на время подцепиться к потоку активного окна.
                let fg = GetForegroundWindow();
                let theirs = GetWindowThreadProcessId(fg, null_mut());
                let ours = GetCurrentThreadId();
                AttachThreadInput(ours, theirs, 1);
                BringWindowToTop(h);
                SetForegroundWindow(h);
                AttachThreadInput(ours, theirs, 0);
            }
        }
        schedule();
        Ok(())
    }

    pub fn minimize(hwnd: i64) -> Result<(), String> {
        let h = window(hwnd)?;
        unsafe { ShowWindowAsync(h, SW_MINIMIZE) };
        Ok(())
    }

    pub fn close(hwnd: i64) -> Result<(), String> {
        let h = window(hwnd)?;
        unsafe { PostMessageW(h, WM_CLOSE, 0, 0) };
        Ok(())
    }

    /// `{GUID}\путь\app.exe` — так «Пуск» называет программы без своего AUMID.
    fn known_folder_path(id: &str) -> Option<String> {
        let rest = id.strip_prefix('{')?;
        let (guid, tail) = rest.split_once("}\\")?;
        let guid = GUID::try_from(guid).ok()?;
        unsafe {
            let p = SHGetKnownFolderPath(&guid, KF_FLAG_DEFAULT, None).ok()?;
            let base = p.to_string().ok();
            CoTaskMemFree(Some(p.0 as *const _));
            Some(format!("{}\\{tail}", base?))
        }
    }

    /// Есть ли приложение и где его exe (у ярлыков — цель ярлыка).
    pub fn target(target: &str) -> AppTarget {
        if target.is_empty() {
            return AppTarget {
                exists: false,
                exe: None,
            };
        }
        if is_path(target) {
            let exists = Path::new(target).exists();
            let exe = if target.to_lowercase().ends_with(".lnk") {
                shell_item(target).and_then(|i| item_string(&i, &PKEY_Link_TargetParsingPath))
            } else {
                Some(target.to_string())
            };
            return AppTarget { exists, exe };
        }
        let Some(item) = shell_item(&format!("shell:AppsFolder\\{target}")) else {
            return AppTarget {
                exists: false,
                exe: None,
            };
        };
        let exe = item_string(&item, &PKEY_Link_TargetParsingPath)
            .filter(|p| is_path(p))
            .or_else(|| known_folder_path(target));
        AppTarget { exists: true, exe }
    }
}

/// Вне Windows окон нет: сборка и тесты на других системах.
#[cfg(not(windows))]
mod os {
    use super::{is_path, AppTarget};

    pub fn activate(_: i64) -> Result<(), String> {
        Err("окна есть только в Windows".into())
    }

    pub fn minimize(_: i64) -> Result<(), String> {
        Err("окна есть только в Windows".into())
    }

    pub fn close(_: i64) -> Result<(), String> {
        Err("окна есть только в Windows".into())
    }

    pub fn target(target: &str) -> AppTarget {
        let exists =
            !target.is_empty() && (!is_path(target) || std::path::Path::new(target).exists());
        AppTarget {
            exists,
            exe: is_path(target).then(|| target.to_string()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn normal() -> Facts {
        Facts {
            visible: true,
            class: "Chrome_WidgetWin_1".into(),
            title: "Почта — Chrome".into(),
            ..Facts::default()
        }
    }

    #[test]
    fn ordinary_windows_are_shown() {
        assert!(is_app_window(&normal()));
    }

    #[test]
    fn hidden_cloaked_tool_owned_and_own_windows_are_not() {
        for f in [
            Facts {
                visible: false,
                ..normal()
            },
            Facts {
                cloaked: true,
                ..normal()
            },
            Facts {
                tool: true,
                ..normal()
            },
            Facts {
                has_owner: true,
                ..normal()
            },
            Facts {
                no_activate: true,
                ..normal()
            },
            Facts {
                ours: true,
                ..normal()
            },
            Facts {
                title: "  ".into(),
                ..normal()
            },
            Facts {
                class: "Progman".into(),
                ..normal()
            },
        ] {
            assert!(!is_app_window(&f), "{f:?}");
        }
    }

    #[test]
    fn app_window_style_wins_over_tool_and_owner() {
        assert!(is_app_window(&Facts {
            tool: true,
            has_owner: true,
            app_window: true,
            ..normal()
        }));
    }

    #[test]
    fn app_name_prefers_start_menu_then_description() {
        let exe = Some("C:\\Apps\\tg\\Telegram.exe");
        assert_eq!(
            app_name(Some("Telegram"), Some("Desktop"), exe, "t"),
            "Telegram"
        );
        assert_eq!(
            app_name(None, Some("Telegram Desktop"), exe, "t"),
            "Telegram Desktop"
        );
        assert_eq!(app_name(None, Some(" "), exe, "t"), "Telegram");
        assert_eq!(app_name(None, None, None, "Окно"), "Окно");
    }

    fn win(hwnd: i64) -> WindowInfo {
        WindowInfo {
            hwnd,
            title: format!("w{hwnd}"),
            aumid: None,
            exe: None,
            app_name: String::new(),
            minimized: false,
            active: false,
            attention: false,
            seq: 0,
            focused: 0,
        }
    }

    #[test]
    fn memory_keeps_order_focus_and_attention() {
        let mut m = Memory::default();
        let mut list = vec![win(1), win(2)];
        m.apply(&mut list);
        let (s1, s2) = (list[0].seq, list[1].seq);
        assert!(s1 < s2);

        m.flash(2);
        m.activated(1);
        let mut list = vec![win(2), win(1), win(3)];
        m.apply(&mut list);
        // Порядок появления не зависит от z-order; новое окно — последним.
        assert_eq!((list[1].seq, list[0].seq), (s1, s2));
        assert!(list[2].seq > s2);
        assert!(list[1].active && !list[0].active);
        assert!(list[0].attention);
        assert!(list[1].focused > 0 && list[0].focused == 0);

        // Окно стало активным — больше не мигает; активное окно мигать не может.
        m.activated(2);
        m.flash(2);
        let mut list = vec![win(1), win(2)];
        m.apply(&mut list);
        assert!(!list[1].attention && list[1].active);
        assert!(list[1].focused > list[0].focused);
    }

    #[test]
    fn closed_windows_are_forgotten() {
        let mut m = Memory::default();
        m.flash(5);
        let mut list = vec![win(5)];
        m.apply(&mut list);
        m.apply(&mut []);
        let mut list = vec![win(5)];
        m.apply(&mut list);
        assert!(!list[0].attention);
    }
}
