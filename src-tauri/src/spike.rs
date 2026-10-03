//! Спайки WSH-4 (стекло под доком) и WSH-5 (окно без активации, topmost).
//! Только для `--features spike`, в продукт не идёт.
//!
//! Вариант выбирается переменной `WS_SPIKE`:
//! - `a`       — два окна: снизу подложка с DWM Acrylic по размеру таблетки, сверху прозрачное окно иконок;
//! - `a-blur`  — как `a`, но размытие через `SetWindowCompositionAttribute` (не зависит от активности окна);
//! - `b`       — одно окно с Acrylic, обрезанное `SetWindowRgn` по таблетке;
//! - `c`       — без размытия: тонировка, обводка и тень средствами CSS.
//!
//! `WS_NOBG=1` — без контрастного тестового фона, `WS_NORAISE=1` — без возврата наверх по хуку.

use std::sync::atomic::{AtomicI32, AtomicIsize, Ordering};
use tauri::{AppHandle, Manager, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindowBuilder};
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Dwm::{
    DwmSetWindowAttribute, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND,
};
use windows_sys::Win32::Graphics::Gdi::{CreateRoundRectRgn, SetWindowRgn};
use windows_sys::Win32::UI::Accessibility::{SetWinEventHook, HWINEVENTHOOK};
use windows_sys::Win32::UI::Shell::{
    DefSubclassProc, SHQueryUserNotificationState, SetWindowSubclass,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    GetWindowLongPtrW, SendMessageW, SetForegroundWindow, SetWindowLongPtrW, SetWindowPos,
    EVENT_SYSTEM_FOREGROUND, GWL_EXSTYLE, HWND_TOPMOST, MA_NOACTIVATE, SWP_NOACTIVATE, SWP_NOMOVE,
    SWP_NOOWNERZORDER, SWP_NOSIZE, SWP_NOZORDER, WINEVENT_OUTOFCONTEXT, WM_DISPLAYCHANGE,
    WM_DPICHANGED, WM_MOUSEACTIVATE, WM_NCACTIVATE, WM_SETTINGCHANGE, WS_EX_NOACTIVATE,
};

/// Окна дока снизу вверх: подложка (может не быть), иконки.
static DOCK: [AtomicIsize; 2] = [AtomicIsize::new(0), AtomicIsize::new(0)];
/// Последнее чужое активное окно: ему возвращаем фокус после клика по доку.
static PREV_FG: AtomicIsize = AtomicIsize::new(0);
/// Таблетка в физических пикселях относительно окна иконок: x, y, ширина.
static PILL: [AtomicI32; 3] = [AtomicI32::new(0), AtomicI32::new(0), AtomicI32::new(0)];

pub(crate) fn dock_hwnds() -> [HWND; 2] {
    [0, 1].map(|i| DOCK[i].load(Ordering::Relaxed) as HWND)
}

/// Смещение и ширина таблетки внутри окна иконок.
pub(crate) fn pill_offset() -> (i32, i32) {
    (
        PILL[0].load(Ordering::Relaxed),
        PILL[2].load(Ordering::Relaxed),
    )
}

/// Левый верхний угол таблетки (и подложки) внутри окна иконок.
pub(crate) fn pill_origin() -> (i32, i32) {
    (
        PILL[0].load(Ordering::Relaxed),
        PILL[1].load(Ordering::Relaxed),
    )
}

const ICONS: &str = "spike-icons";
const BACKDROP: &str = "spike-backdrop";
/// Окно иконок выше таблетки: место под увеличение.
pub(crate) const ICONS_W: f64 = 1200.0;
pub(crate) const ICONS_H: f64 = 190.0;
/// Тонировка подложки поверх размытия (RGBA).
const TINT: (u8, u8, u8, u8) = (14, 30, 43, 90);

fn variant() -> String {
    std::env::var("WS_SPIKE").unwrap_or_else(|_| "a".into())
}

fn hwnd(w: &tauri::WebviewWindow) -> HWND {
    w.hwnd().expect("hwnd").0
}

/// Клик по окну не забирает фокус у активного приложения.
/// Этого мало (проверено): WebView2 на клик сам зовёт `SetFocus` своему окну ввода, и док
/// становится активным. Поэтому ещё `on_foreground` сразу возвращает фокус предыдущему окну.
fn no_activate(w: &tauri::WebviewWindow) {
    unsafe {
        let ex = GetWindowLongPtrW(hwnd(w), GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd(w), GWL_EXSTYLE, ex | WS_EX_NOACTIVATE as isize);
        SetWindowSubclass(hwnd(w), Some(no_mouse_activate), 2, 0);
    }
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
    if matches!(msg, WM_DISPLAYCHANGE | WM_DPICHANGED | WM_SETTINGCHANGE) {
        // Сначала Tauri применяет свой размер под новый DPI, потом мы раскладываем поверх (проверено).
        let r = DefSubclassProc(hwnd, msg, wparam, lparam);
        eprintln!("[spike] display message {msg:#x}");
        crate::spike_autohide::RELAYOUT.store(true, Ordering::Relaxed);
        return r;
    }
    DefSubclassProc(hwnd, msg, wparam, lparam)
}

/// Поднимает док над другими topmost-окнами, не активируя его.
fn raise_dock() {
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
            std::thread::sleep(std::time::Duration::from_millis(50));
            let b = DOCK[0].load(Ordering::Relaxed);
            if b != 0 {
                unsafe { SendMessageW(b as HWND, WM_NCACTIVATE, 1, 0) };
            }
        });
        return;
    }
    PREV_FG.store(h, Ordering::Relaxed);
    let mut quns = 0;
    SHQueryUserNotificationState(&mut quns);
    // 2 — занят (полноэкранное окно), 3 — D3D-полноэкранная игра, 4 — презентация, 5 — обычный режим.
    eprintln!("[spike] foreground {fg:?}, notification state {quns}");
    if std::env::var("WS_NORAISE").is_err() {
        // Новое окно становится активным раньше, чем ставит себе TOPMOST (проверено),
        // поэтому поднимаемся ещё раз чуть позже.
        // ponytail: две фиксированные задержки; если не хватит — ловить EVENT_OBJECT_REORDER.
        raise_dock();
        std::thread::spawn(|| {
            for ms in [80, 300] {
                std::thread::sleep(std::time::Duration::from_millis(ms));
                raise_dock();
            }
        });
    }
}

fn builder<'a>(
    app: &'a AppHandle,
    label: &'a str,
    role: &str,
    v: &str,
) -> WebviewWindowBuilder<'a, tauri::Wry, AppHandle> {
    let b = WebviewWindowBuilder::new(app, label, WebviewUrl::App("index.html".into()))
        .initialization_script(format!(
            "window.__WS_SPIKE__ = {{ role: {role:?}, variant: {v:?} }};"
        ))
        .decorations(false)
        .transparent(true)
        .shadow(false)
        .resizable(false)
        .skip_taskbar(true)
        .always_on_top(true)
        .focused(false);
    // `WS_CREATE_AT=x,y` (физические пиксели): создать окно сразу на мониторе с этой точкой.
    // Проверяем, нужен ли WebView2 «родной» монитор с момента создания.
    match create_at(app) {
        Some((x, y)) => b.position(x, y),
        None => b,
    }
}

fn create_at(app: &AppHandle) -> Option<(f64, f64)> {
    let v = std::env::var("WS_CREATE_AT").ok()?;
    let (x, y) = v.split_once(',')?;
    let (x, y): (f64, f64) = (x.trim().parse().ok()?, y.trim().parse().ok()?);
    let m = app.monitor_from_point(x, y).ok()??;
    let s = m.scale_factor();
    Some((x / s, y / s))
}

pub fn setup(app: &AppHandle) -> tauri::Result<()> {
    let v = variant();
    let monitor = app.primary_monitor()?.expect("primary monitor");
    let scale = monitor.scale_factor();
    let area = monitor.work_area();
    let (w, h) = ((ICONS_W * scale) as i32, (ICONS_H * scale) as i32);
    let x = area.position.x + (area.size.width as i32 - w) / 2;
    let y = area.position.y + area.size.height as i32 - h;

    // Контрастный «фон» под доком, чтобы размытие было видно. Не поверх всех, без фокуса.
    if std::env::var("WS_NOBG").is_err() {
        let bg = builder(app, "spike-bg", "bg", &v)
            .always_on_top(false)
            .visible(false)
            .build()?;
        bg.set_size(PhysicalSize::new(area.size.width, (420.0 * scale) as u32))?;
        bg.set_position(PhysicalPosition::new(
            area.position.x,
            area.position.y + area.size.height as i32 - (420.0 * scale) as i32,
        ))?;
        bg.show()?;
    }

    let backdrop = if v.starts_with('a') {
        let b = builder(app, BACKDROP, "backdrop", &v)
            .visible(false)
            .build()?;
        match v.as_str() {
            "a-blur" => window_vibrancy::apply_blur(&b, Some(TINT)),
            _ => window_vibrancy::apply_acrylic(&b, Some(TINT)),
        }
        .expect("blur");
        no_activate(&b);
        DOCK[0].store(hwnd(&b) as isize, Ordering::Relaxed);
        Some(b)
    } else {
        None
    };

    // Иконки не делаем «владеемыми» подложкой: иначе краткая активация иконок при клике
    // гасит Acrylic у подложки насовсем (проверено). Порядок держит `raise_dock`.
    let icons = builder(app, ICONS, "icons", &v).visible(false).build()?;
    // Клик по доку не должен забирать фокус (и гасить Acrylic у подложки).
    no_activate(&icons);
    DOCK[1].store(hwnd(&icons) as isize, Ordering::Relaxed);
    if create_at(app).is_none() {
        icons.set_size(PhysicalSize::new(w as u32, h as u32))?;
        icons.set_position(PhysicalPosition::new(x, y))?;
    }
    if v == "b" {
        window_vibrancy::apply_acrylic(&icons, Some(TINT)).expect("acrylic");
    }
    if let Some(b) = &backdrop {
        b.show()?;
    }
    icons.show()?;
    // `WS_MOVE_TAURI=x,y` — переезд на монитор с этой точкой средствами Tauri (из главного потока).
    if let Some((mx, my)) = std::env::var("WS_MOVE_TAURI")
        .ok()
        .and_then(|v| {
            v.split_once(',')
                .map(|(a, b)| (a.trim().parse::<i32>(), b.trim().parse::<i32>()))
        })
        .and_then(|(a, b)| Some((a.ok()?, b.ok()?)))
    {
        let icons = icons.clone();
        std::thread::spawn(move || {
            std::thread::sleep(std::time::Duration::from_secs(2));
            let _ = icons.set_position(PhysicalPosition::new(mx, my));
        });
    }
    if let Some(b) = &backdrop {
        // Регион DWM-подложку не обрезает (проверено), скругление — только штатное, 8 px.
        let round = DWMWCP_ROUND;
        unsafe {
            DwmSetWindowAttribute(
                hwnd(b),
                DWMWA_WINDOW_CORNER_PREFERENCE as u32,
                &round as *const _ as *const _,
                std::mem::size_of_val(&round) as u32,
            )
        };
        // DWM рисует Acrylic только у активного окна. Док активным не бывает,
        // поэтому говорим подложке, что она «активна», не отдавая ей фокус.
        // Подмена в обработчике держит это и тогда, когда Windows шлёт «неактивна».
        unsafe {
            SetWindowSubclass(hwnd(b), Some(always_active), 1, 0);
            SendMessageW(hwnd(b), WM_NCACTIVATE, 1, 0);
        }
    }

    eprintln!(
        "[spike] dock hwnds: backdrop {:#x}, icons {:#x}",
        DOCK[0].load(Ordering::Relaxed),
        DOCK[1].load(Ordering::Relaxed)
    );
    // Кто-то стал активным (в том числе новое topmost-окно) — возвращаем док наверх.
    unsafe {
        SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            std::ptr::null_mut(),
            Some(on_foreground),
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        )
    };

    for m in app.available_monitors()? {
        eprintln!(
            "[spike] monitor {:?}: {:?} {:?}, work {:?}, scale {}",
            m.name(),
            m.position(),
            m.size(),
            m.work_area(),
            m.scale_factor()
        );
    }
    if std::env::var("WS_AUTOHIDE").is_ok() {
        crate::spike_autohide::start();
    }

    if let Some(main) = app.get_webview_window("main") {
        main.close()?;
    }
    Ok(())
}

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

/// Фронт сообщает прямоугольник таблетки в физических пикселях окна иконок, каждый кадр, когда он меняется.
#[tauri::command]
pub fn spike_pill(
    app: AppHandle,
    x: f64,
    y: f64,
    w: f64,
    h: f64,
    radius: f64,
) -> Result<(), String> {
    let icons = app.get_webview_window(ICONS).ok_or("no icons window")?;
    // Фронт уже прислал физические пиксели.
    let s = 1.0;
    let (px, py, pw, ph, pr) = (
        (x * s).round() as i32,
        (y * s).round() as i32,
        (w * s).round() as i32,
        (h * s).round() as i32,
        (radius * 2.0 * s).round() as i32,
    );
    for (slot, v) in PILL.iter().zip([px, py, pw]) {
        slot.store(v, Ordering::Relaxed);
    }
    // ponytail: регион без сглаживания — это и есть то, что спайк проверяет глазами.
    unsafe {
        match app.get_webview_window(BACKDROP) {
            Some(b) => {
                let origin = icons.outer_position().map_err(|e| e.to_string())?;
                SetWindowPos(
                    hwnd(&b),
                    std::ptr::null_mut(),
                    origin.x + px,
                    origin.y + py,
                    pw,
                    ph,
                    SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER,
                );
            }
            None if variant() == "b" => {
                let rgn = CreateRoundRectRgn(px, py, px + pw + 1, py + ph + 1, pr, pr);
                SetWindowRgn(hwnd(&icons), rgn, 1);
            }
            None => {}
        }
    }
    Ok(())
}
