//! Док — два окна: снизу подложка с Acrylic по размеру таблетки, сверху прозрачное окно иконок.
//! Всё Win32 здесь — решения спайков WSH-4 (стекло) и WSH-5 (без активации, topmost),
//! подробности в `docs/ARCHITECTURE.md`.

use std::sync::atomic::{AtomicIsize, Ordering};
use std::time::Duration;

use tauri::WebviewWindow;
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Dwm::{
    DwmSetWindowAttribute, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND,
};
use windows_sys::Win32::UI::Accessibility::{SetWinEventHook, HWINEVENTHOOK};
use windows_sys::Win32::UI::Shell::{DefSubclassProc, SetWindowSubclass};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    GetWindowLongPtrW, GetWindowThreadProcessId, SendMessageW, SetForegroundWindow,
    SetWindowLongPtrW, SetWindowPos, EVENT_SYSTEM_FOREGROUND, GWL_EXSTYLE, HWND_TOPMOST,
    MA_NOACTIVATE, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOOWNERZORDER, SWP_NOSIZE, SWP_NOZORDER,
    WINEVENT_OUTOFCONTEXT, WM_MOUSEACTIVATE, WM_NCACTIVATE, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
};

/// Тонировка подложки поверх размытия (RGBA).
const TINT: (u8, u8, u8, u8) = (14, 30, 43, 90);

/// Окна дока снизу вверх: подложка, иконки.
static DOCK: [AtomicIsize; 2] = [AtomicIsize::new(0), AtomicIsize::new(0)];
/// Последнее чужое активное окно: ему возвращаем фокус после клика по доку.
static PREV_FG: AtomicIsize = AtomicIsize::new(0);

fn hwnd(w: &WebviewWindow) -> HWND {
    w.hwnd().expect("hwnd").0
}

fn add_ex_style(h: HWND, style: u32) {
    unsafe {
        let ex = GetWindowLongPtrW(h, GWL_EXSTYLE);
        SetWindowLongPtrW(h, GWL_EXSTYLE, ex | style as isize);
    }
}

/// Окно оболочки не показывается в Alt+Tab.
pub fn tool_window(w: &WebviewWindow) {
    add_ex_style(hwnd(w), WS_EX_TOOLWINDOW);
}

/// `WS_EX_NOACTIVATE` + `MA_NOACTIVATE`. Клик по WebView2 всё равно активирует окно
/// (WebView2 сам зовёт `SetFocus`), поэтому ещё `on_foreground` возвращает фокус.
fn no_activate(h: HWND) {
    add_ex_style(h, WS_EX_NOACTIVATE);
    unsafe { SetWindowSubclass(h, Some(no_mouse_activate), 2, 0) };
}

unsafe extern "system" fn no_mouse_activate(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
    _id: usize,
    _data: usize,
) -> LRESULT {
    if msg == WM_MOUSEACTIVATE {
        return MA_NOACTIVATE as LRESULT;
    }
    DefSubclassProc(hwnd, msg, wparam, lparam)
}

/// DWM рисует Acrylic только у «активного» окна: держим подложку активной, не отдавая ей фокус.
unsafe extern "system" fn always_active(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
    _id: usize,
    _data: usize,
) -> LRESULT {
    let wparam = if msg == WM_NCACTIVATE { 1 } else { wparam };
    DefSubclassProc(hwnd, msg, wparam, lparam)
}

fn reactivate_backdrop() {
    let b = DOCK[0].load(Ordering::Relaxed);
    if b != 0 {
        unsafe { SendMessageW(b as HWND, WM_NCACTIVATE, 1, 0) };
    }
}

/// Поднимает док над другими topmost-окнами, не активируя его: сначала подложку, потом иконки.
fn raise() {
    for slot in &DOCK {
        let h = slot.load(Ordering::Relaxed);
        if h != 0 {
            unsafe {
                SetWindowPos(
                    h as HWND,
                    HWND_TOPMOST,
                    0,
                    0,
                    0,
                    0,
                    SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE,
                )
            };
        }
    }
}

fn is_ours(h: HWND) -> bool {
    let mut pid = 0;
    unsafe { GetWindowThreadProcessId(h, &mut pid) };
    pid == std::process::id()
}

unsafe extern "system" fn on_foreground(
    _hook: HWINEVENTHOOK,
    _event: u32,
    fg: HWND,
    _obj: i32,
    _child: i32,
    _thread: u32,
    _time: u32,
) {
    let h = fg as isize;
    if DOCK.iter().any(|slot| slot.load(Ordering::Relaxed) == h) {
        // Активным стал сам док (клик по WebView2). Мы сейчас foreground — вправе отдать фокус.
        let prev = PREV_FG.load(Ordering::Relaxed);
        if prev != 0 {
            SetForegroundWindow(prev as HWND);
        }
        // Потеря активности гасит Acrylic у подложки — включаем обратно, когда фокус ушёл.
        std::thread::spawn(|| {
            std::thread::sleep(Duration::from_millis(50));
            reactivate_backdrop();
        });
        return;
    }
    // Свои окна (панель, поповеры) не запоминаем: фокус возвращаем только чужому приложению.
    if !is_ours(fg) {
        PREV_FG.store(h, Ordering::Relaxed);
    }
    // Чужое окно становится активным раньше, чем ставит себе TOPMOST, — поднимаемся ещё раз позже.
    // ponytail: две фиксированные задержки; если не хватит — ловить EVENT_OBJECT_REORDER.
    raise();
    std::thread::spawn(|| {
        for ms in [80, 300] {
            std::thread::sleep(Duration::from_millis(ms));
            raise();
        }
    });
}

/// Настроить окна дока. Вызывается один раз из главного потока после создания окон.
pub fn setup(backdrop: &WebviewWindow, icons: &WebviewWindow) {
    if let Err(e) = window_vibrancy::apply_acrylic(backdrop, Some(TINT)) {
        eprintln!("[dock] acrylic unavailable: {e}");
    }
    let (b, i) = (hwnd(backdrop), hwnd(icons));
    // Иконки не «владеемые» подложкой: иначе краткая активация иконок гасит Acrylic насовсем.
    no_activate(b);
    no_activate(i);
    DOCK[0].store(b as isize, Ordering::Relaxed);
    DOCK[1].store(i as isize, Ordering::Relaxed);
    let round = DWMWCP_ROUND;
    unsafe {
        DwmSetWindowAttribute(
            b,
            DWMWA_WINDOW_CORNER_PREFERENCE as u32,
            &round as *const _ as *const _,
            std::mem::size_of_val(&round) as u32,
        );
        SetWindowSubclass(b, Some(always_active), 1, 0);
        SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            std::ptr::null_mut(),
            Some(on_foreground),
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        );
    }
    reactivate_backdrop();
    raise();
}

/// Фронт окна иконок сообщает прямоугольник таблетки в физических пикселях окна
/// (`devicePixelRatio` самого WebView2, см. спайк WSH-6) — подложка встаёт ровно под неё.
pub fn place_backdrop(icons: &WebviewWindow, x: i32, y: i32, w: i32, h: i32) -> tauri::Result<()> {
    let b = DOCK[0].load(Ordering::Relaxed);
    if b == 0 {
        return Ok(());
    }
    let origin = icons.outer_position()?;
    unsafe {
        SetWindowPos(
            b as HWND,
            std::ptr::null_mut(),
            origin.x + x,
            origin.y + y,
            w,
            h,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER,
        )
    };
    Ok(())
}
