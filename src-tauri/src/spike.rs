//! Спайк WSH-4: стекло под доком. Только для `--features spike`, в продукт не идёт.
//!
//! Вариант выбирается переменной `WS_SPIKE`:
//! - `a`       — два окна: снизу подложка с DWM Acrylic по размеру таблетки, сверху прозрачное окно иконок;
//! - `a-blur`  — как `a`, но размытие через `SetWindowCompositionAttribute` (не зависит от активности окна);
//! - `b`       — одно окно с Acrylic, обрезанное `SetWindowRgn` по таблетке;
//! - `c`       — без размытия: тонировка, обводка и тень средствами CSS.

use tauri::{AppHandle, Manager, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindowBuilder};
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::Graphics::Dwm::{
    DwmSetWindowAttribute, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND,
};
use windows_sys::Win32::Graphics::Gdi::{CreateRoundRectRgn, SetWindowRgn};
use windows_sys::Win32::UI::Shell::{DefSubclassProc, SetWindowSubclass};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    GetWindowLongPtrW, SendMessageW, SetWindowLongPtrW, SetWindowPos, GWL_EXSTYLE, SWP_NOACTIVATE,
    SWP_NOOWNERZORDER, SWP_NOZORDER, WM_NCACTIVATE, WS_EX_NOACTIVATE,
};

const ICONS: &str = "spike-icons";
const BACKDROP: &str = "spike-backdrop";
/// Окно иконок выше таблетки: место под увеличение.
const ICONS_W: f64 = 1200.0;
const ICONS_H: f64 = 190.0;
/// Тонировка подложки поверх размытия (RGBA).
const TINT: (u8, u8, u8, u8) = (14, 30, 43, 90);

fn variant() -> String {
    std::env::var("WS_SPIKE").unwrap_or_else(|_| "a".into())
}

fn hwnd(w: &tauri::WebviewWindow) -> HWND {
    w.hwnd().expect("hwnd").0
}

fn builder<'a>(
    app: &'a AppHandle,
    label: &'a str,
    role: &str,
    v: &str,
) -> WebviewWindowBuilder<'a, tauri::Wry, AppHandle> {
    WebviewWindowBuilder::new(app, label, WebviewUrl::App("index.html".into()))
        .initialization_script(format!(
            "window.__WS_SPIKE__ = {{ role: {role:?}, variant: {v:?} }};"
        ))
        .decorations(false)
        .transparent(true)
        .shadow(false)
        .resizable(false)
        .skip_taskbar(true)
        .always_on_top(true)
        .focused(false)
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

    let backdrop = if v.starts_with('a') {
        let b = builder(app, BACKDROP, "backdrop", &v)
            .visible(false)
            .build()?;
        match v.as_str() {
            "a-blur" => window_vibrancy::apply_blur(&b, Some(TINT)),
            _ => window_vibrancy::apply_acrylic(&b, Some(TINT)),
        }
        .expect("blur");
        Some(b)
    } else {
        None
    };

    let mut icons = builder(app, ICONS, "icons", &v);
    if let Some(b) = &backdrop {
        // Владелец всегда ниже своих окон: иконки гарантированно над подложкой.
        icons = icons.owner(b)?;
    }
    let icons = icons.visible(false).build()?;
    // Клик по доку не должен забирать фокус (и гасить Acrylic у подложки).
    unsafe {
        let ex = GetWindowLongPtrW(hwnd(&icons), GWL_EXSTYLE);
        SetWindowLongPtrW(hwnd(&icons), GWL_EXSTYLE, ex | WS_EX_NOACTIVATE as isize);
    }
    icons.set_size(PhysicalSize::new(w as u32, h as u32))?;
    icons.set_position(PhysicalPosition::new(x, y))?;
    if v == "b" {
        window_vibrancy::apply_acrylic(&icons, Some(TINT)).expect("acrylic");
    }
    if let Some(b) = &backdrop {
        b.show()?;
    }
    icons.show()?;
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

/// Фронт сообщает прямоугольник таблетки в CSS-пикселях окна иконок, каждый кадр, когда он меняется.
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
    let s = icons.scale_factor().map_err(|e| e.to_string())?;
    let (px, py, pw, ph, pr) = (
        (x * s).round() as i32,
        (y * s).round() as i32,
        (w * s).round() as i32,
        (h * s).round() as i32,
        (radius * 2.0 * s).round() as i32,
    );
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
                SetWindowRgn(
                    hwnd(&b),
                    CreateRoundRectRgn(0, 0, pw + 1, ph + 1, pr, pr),
                    1,
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
