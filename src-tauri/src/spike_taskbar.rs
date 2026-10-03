//! Спайк WSH-43: прячем панель задач Windows и гарантированно возвращаем её.
//! Включается `WS_TASKBAR=hide` поверх спайка дока (`--features spike`). Сторож и возврат
//! общие со спайком трея WSH-44 (`WS_TRAY=1`).
//!
//! Возврат, от самого частого к аварийному:
//! 1. обычный выход приложения (`RunEvent::Exit`) и паника;
//! 2. процесс-сторож (`--taskbar-watchdog <pid>`): ждёт смерти основного процесса и возвращает;
//! 3. следующий запуск: файл состояния остался — значит, прошлый раз не вернули;
//! 4. `scripts/restore-taskbar.cmd` — без нашего exe вообще.

use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::time::Duration;

use windows_sys::Win32::Foundation::{CloseHandle, HWND};
use windows_sys::Win32::System::Threading::{
    OpenProcess, WaitForSingleObject, INFINITE, PROCESS_SYNCHRONIZE,
};
use windows_sys::Win32::UI::Shell::{
    SHAppBarMessage, ABM_GETSTATE, ABM_SETSTATE, ABS_AUTOHIDE, APPBARDATA,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    FindWindowExW, IsWindowVisible, RegisterWindowMessageW, SendNotifyMessageW, ShowWindow,
    HWND_BROADCAST, SW_HIDE, SW_SHOW,
};

/// Сейчас прячем: фоновая проверка и `TaskbarCreated` прячут панель заново.
static HIDING: AtomicBool = AtomicBool::new(false);
/// Номер сообщения `TaskbarCreated` (Explorer рассылает его после перезапуска).
pub static TASKBAR_CREATED: AtomicU32 = AtomicU32::new(0);

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain([0]).collect()
}

/// Файл с исходной настройкой автоскрытия. Есть файл — панель сейчас «наша» и её надо вернуть.
fn state_file() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA").map_or_else(std::env::temp_dir, PathBuf::from);
    base.join("WindowsShell").join("taskbar-state.txt")
}

pub(crate) fn log(msg: &str) {
    eprintln!("[taskbar] {msg}");
    let path = state_file().with_file_name("taskbar.log");
    let line = format!(
        "{:?} pid {} {msg}\n",
        std::time::SystemTime::now(),
        std::process::id()
    );
    let _ = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .and_then(|mut f| std::io::Write::write_all(&mut f, line.as_bytes()));
}

/// Окна панели задач на всех мониторах.
fn tray_windows() -> Vec<HWND> {
    let mut out = Vec::new();
    for class in ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"] {
        let class = wide(class);
        let mut h: HWND = std::ptr::null_mut();
        loop {
            h = unsafe { FindWindowExW(std::ptr::null_mut(), h, class.as_ptr(), std::ptr::null()) };
            if h.is_null() {
                break;
            }
            out.push(h);
        }
    }
    out
}

fn appbar(msg: u32, lparam: isize) -> usize {
    let mut d: APPBARDATA = unsafe { std::mem::zeroed() };
    d.cbSize = std::mem::size_of::<APPBARDATA>() as u32;
    d.hWnd = tray_windows()
        .first()
        .copied()
        .unwrap_or(std::ptr::null_mut());
    d.lParam = lparam;
    unsafe { SHAppBarMessage(msg, &mut d) }
}

fn set_visible(show: bool) -> usize {
    let wins = tray_windows();
    for &h in &wins {
        unsafe { ShowWindow(h, if show { SW_SHOW } else { SW_HIDE }) };
    }
    wins.len()
}

/// Вернуть панель задач. Безопасно звать сколько угодно раз и из любого процесса.
pub fn restore() {
    HIDING.store(false, Ordering::Relaxed);
    let file = state_file();
    if let Ok(saved) = std::fs::read_to_string(&file) {
        let autohide = saved.contains("autohide=1");
        appbar(
            ABM_SETSTATE,
            if autohide { ABS_AUTOHIDE as isize } else { 0 },
        );
        let _ = std::fs::remove_file(&file);
        log(&format!("restored autohide={}", autohide as u8));
        if saved.contains("tray=1") {
            // Наш трей перехватывал иконки. Его окно уже закрыто — просим программы
            // заново зарегистрировать иконки у Explorer (так делает сам Explorer после перезапуска).
            crate::spike_tray::stop();
            unsafe {
                SendNotifyMessageW(
                    HWND_BROADCAST,
                    RegisterWindowMessageW(wide("TaskbarCreated").as_ptr()),
                    0,
                    0,
                )
            };
            log("broadcast TaskbarCreated");
        }
    }
    let n = set_visible(true);
    log(&format!("shown {n} taskbar windows"));
}

/// Режимы командной строки. `true` — режим отработал, приложение не запускаем.
pub fn cli() -> bool {
    let args: Vec<String> = std::env::args().collect();
    match args.get(1).map(String::as_str) {
        Some("--restore-taskbar") => {
            restore();
            true
        }
        Some("--taskbar-watchdog") => {
            let pid: u32 = args.get(2).and_then(|p| p.parse().ok()).unwrap_or(0);
            log(&format!("watchdog waiting for pid {pid}"));
            unsafe {
                let h = OpenProcess(PROCESS_SYNCHRONIZE, 0, pid);
                if !h.is_null() {
                    WaitForSingleObject(h, INFINITE);
                    CloseHandle(h);
                }
            }
            log("watchdog: main process exited");
            restore();
            true
        }
        _ => false,
    }
}

fn hide_again(reason: &str) {
    let visible = tray_windows()
        .iter()
        .any(|&h| unsafe { IsWindowVisible(h) } != 0);
    if HIDING.load(Ordering::Relaxed) && visible {
        set_visible(false);
        log(&format!("hidden again ({reason})"));
    }
}

/// Вызывается из обработчика сообщений окна дока.
pub fn on_message(msg: u32) {
    let created = TASKBAR_CREATED.load(Ordering::Relaxed);
    if created != 0 && msg == created {
        // Explorer перезапустился и показал новую панель — её автоскрытие тоже надо вернуть.
        if HIDING.load(Ordering::Relaxed) {
            appbar(ABM_SETSTATE, ABS_AUTOHIDE as isize);
        }
        hide_again("TaskbarCreated");
    }
}

/// Перед любыми изменениями: сохранить исходное состояние, поднять сторожа, повесить panic hook.
/// `tray` — будет ли работать наш трей (тогда при возврате программы перерегистрируют иконки).
pub fn guard(tray: bool) {
    // Хвост от прошлого раза (жёсткое убийство обоих процессов) — сначала вернуть.
    if state_file().exists() {
        log("state file left from previous run");
        restore();
    }
    TASKBAR_CREATED.store(
        unsafe { RegisterWindowMessageW(wide("TaskbarCreated").as_ptr()) },
        Ordering::Relaxed,
    );

    let autohide = appbar(ABM_GETSTATE, 0) as u32 & ABS_AUTOHIDE != 0;
    let file = state_file();
    let _ = std::fs::create_dir_all(file.parent().expect("state dir"));
    std::fs::write(
        &file,
        format!("autohide={}\ntray={}\n", autohide as u8, tray as u8),
    )
    .expect("save taskbar state");
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    // Сторожа не ждём: он должен пережить нас.
    #[allow(clippy::zombie_processes)]
    let watchdog = std::process::Command::new(std::env::current_exe().expect("exe"))
        .args(["--taskbar-watchdog", &std::process::id().to_string()])
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .expect("spawn taskbar watchdog");
    log(&format!(
        "saved autohide={}, watchdog pid {}",
        autohide as u8,
        watchdog.id()
    ));

    let prev = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        restore();
        prev(info);
    }));
}

/// Спрятать панель задач на всех мониторах. Перед этим — `guard`.
pub fn hide() {
    // Автоскрытие отдаёт место панели под рабочую область; само окно прячем поверх.
    appbar(ABM_SETSTATE, ABS_AUTOHIDE as isize);
    HIDING.store(true, Ordering::Relaxed);
    let n = set_visible(false);
    log(&format!("hidden {n} taskbar windows"));

    // Explorer иногда показывает панель сам (Win, смена мониторов) — проверяем дважды в секунду.
    // ponytail: опрос раз в 500 мс; если мигание заметно — EVENT_OBJECT_SHOW на окна панели.
    std::thread::spawn(|| loop {
        std::thread::sleep(Duration::from_millis(500));
        hide_again("poll");
    });
}
