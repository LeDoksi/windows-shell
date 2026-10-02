//! Спайк WSH-6: автоскрытие дока опросом курсора, полноэкранные приложения, мониторы и DPI.
//! Включается `WS_AUTOHIDE=1` поверх спайка дока (`--features spike`), монитор — `WS_MONITOR_AT=x,y`.

use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{FILETIME, HWND, POINT, RECT};
use windows_sys::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MonitorFromPoint, MonitorFromWindow, MONITORINFO, MONITOR_DEFAULTTONEAREST,
    MONITOR_DEFAULTTOPRIMARY,
};
use windows_sys::Win32::System::Threading::{GetCurrentThread, GetThreadTimes};
use windows_sys::Win32::UI::HiDpi::{
    GetAwarenessFromDpiAwarenessContext, GetDpiForMonitor, GetThreadDpiAwarenessContext,
    SetThreadDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, MDT_EFFECTIVE_DPI,
};
use windows_sys::Win32::UI::Shell::SHQueryUserNotificationState;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    BeginDeferWindowPos, DeferWindowPos, EndDeferWindowPos, GetClassNameW, GetCursorPos,
    GetForegroundWindow, GetWindowRect, SendMessageW, SetWindowPos, SWP_NOACTIVATE, SWP_NOSIZE,
    SWP_NOZORDER, WM_NCACTIVATE,
};

use crate::spike::{dock_hwnds, pill_offset, ICONS_H, ICONS_W};

/// Курсор у края дольше этого — показываем (чтобы док не выскакивал, когда курсор пролетает мимо).
pub const REVEAL_DELAY_MS: u64 = 150;
/// Курсор ушёл с дока дольше этого — прячем.
pub const HIDE_DELAY_MS: u64 = 400;
/// Длительность выезда/уезда.
pub const SLIDE_MS: f64 = 180.0;
const TICK: Duration = Duration::from_millis(16);
/// Проверка полноэкранного режима — реже, чем курсор.
const FULLSCREEN_EVERY: u32 = 15;

/// Окна поменяли монитор, DPI или рабочую область — пересчитать раскладку.
pub static RELAYOUT: AtomicBool = AtomicBool::new(true);

/// Прямоугольник в физических пикселях.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Rect {
    pub x: i32,
    pub y: i32,
    pub w: i32,
    pub h: i32,
}

/// Окно иконок по центру низа рабочей области монитора, размер из логических пикселей.
pub fn dock_rect(work: RECT, scale: f64) -> Rect {
    let (w, h) = (
        (ICONS_W * scale).round() as i32,
        (ICONS_H * scale).round() as i32,
    );
    Rect {
        x: work.left + (work.right - work.left - w) / 2,
        y: work.bottom - h,
        w,
        h,
    }
}

fn ease_out(t: f64) -> f64 {
    1.0 - (1.0 - t).powi(3)
}

/// Состояние автоскрытия. `progress`: 0 — спрятан, 1 — показан.
#[derive(Debug, Default)]
pub struct Autohide {
    pub shown: bool,
    edge_since: Option<u64>,
    away_since: Option<u64>,
    progress: f64,
    last: Option<u64>,
}

impl Autohide {
    /// Шаг по времени `now` (мс). Возвращает долю видимости дока с easing: 0..1.
    pub fn tick(&mut self, now: u64, at_edge: bool, over_dock: bool, fullscreen: bool) -> f64 {
        if fullscreen {
            self.shown = false;
            self.edge_since = None;
        } else if self.shown {
            if over_dock {
                self.away_since = None;
            } else if now - *self.away_since.get_or_insert(now) >= HIDE_DELAY_MS {
                self.shown = false;
                self.away_since = None;
            }
        } else if at_edge {
            if now - *self.edge_since.get_or_insert(now) >= REVEAL_DELAY_MS {
                self.shown = true;
                self.edge_since = None;
            }
        } else {
            self.edge_since = None;
        }
        let step = self.last.map_or(0, |l| now - l) as f64 / SLIDE_MS;
        self.last = Some(now);
        self.progress = if self.shown {
            (self.progress + step).min(1.0)
        } else {
            (self.progress - step).max(0.0)
        };
        ease_out(self.progress)
    }
}

struct Layout {
    monitor: RECT,
    dock: Rect,
    scale: f64,
}

unsafe fn layout() -> Layout {
    // `WS_MONITOR_AT=x,y` — монитор, на котором лежит эта точка; иначе основной.
    let at = std::env::var("WS_MONITOR_AT")
        .ok()
        .and_then(|v| {
            let (x, y) = v.split_once(',')?;
            Some(POINT {
                x: x.trim().parse().ok()?,
                y: y.trim().parse().ok()?,
            })
        })
        .unwrap_or(POINT { x: 0, y: 0 });
    let mon = MonitorFromPoint(at, MONITOR_DEFAULTTOPRIMARY);
    let mut info: MONITORINFO = std::mem::zeroed();
    info.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
    GetMonitorInfoW(mon, &mut info);
    let (mut dx, mut dy) = (96, 96);
    GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &mut dx, &mut dy);
    let scale = dx as f64 / 96.0;
    Layout {
        monitor: info.rcMonitor,
        dock: dock_rect(info.rcWork, scale),
        scale,
    }
}

/// Активное чужое окно закрывает весь свой монитор (видео/игра на весь экран).
unsafe fn foreground_covers_monitor(own: [HWND; 2]) -> bool {
    let fg = GetForegroundWindow();
    if fg.is_null() || own.contains(&fg) {
        return false;
    }
    let mut class = [0u16; 64];
    let n = GetClassNameW(fg, class.as_mut_ptr(), class.len() as i32) as usize;
    let class = String::from_utf16_lossy(&class[..n]);
    // Рабочий стол и панель задач тоже «на весь экран», но это не полноэкранное приложение.
    if matches!(class.as_str(), "Progman" | "WorkerW" | "Shell_TrayWnd") {
        return false;
    }
    let mut r: RECT = std::mem::zeroed();
    GetWindowRect(fg, &mut r);
    let mut info: MONITORINFO = std::mem::zeroed();
    info.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
    GetMonitorInfoW(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), &mut info);
    let m = info.rcMonitor;
    r.left <= m.left && r.top <= m.top && r.right >= m.right && r.bottom >= m.bottom
}

unsafe fn notification_busy() -> bool {
    let mut quns = 0;
    SHQueryUserNotificationState(&mut quns);
    // 2 — занят (окно на весь экран), 3 — D3D-игра, 4 — презентация.
    matches!(quns, 2..=4)
}

fn ms(t: FILETIME) -> u64 {
    ((t.dwHighDateTime as u64) << 32 | t.dwLowDateTime as u64) / 10_000
}

unsafe fn thread_cpu_ms() -> u64 {
    let z = std::mem::zeroed::<FILETIME>;
    let (mut c, mut e, mut k, mut u) = (z(), z(), z(), z());
    GetThreadTimes(GetCurrentThread(), &mut c, &mut e, &mut k, &mut u);
    ms(k) + ms(u)
}

/// Поток опроса: ~60 раз в секунду курсор, раз в ~250 мс полноэкранный режим.
pub fn start() {
    std::thread::spawn(|| unsafe {
        // Координаты курсора и окон — в физических пикселях каждого монитора.
        let before = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        eprintln!("[autohide] poll thread DPI awareness was {before}, now per-monitor v2");
        let start = Instant::now();
        let mut state = Autohide::default();
        let mut lay = layout();
        let mut fullscreen = false;
        let (mut cpu_mark, mut wall_mark, mut tick_n) = (thread_cpu_ms(), Instant::now(), 0u32);
        let mut last_dy = i32::MIN;
        loop {
            let [backdrop, icons] = dock_hwnds();
            if RELAYOUT.swap(false, Ordering::Relaxed) {
                lay = layout();
                last_dy = i32::MIN;
                // Сначала ставим окно на сам монитор: Windows шлёт WM_DPICHANGED только окну,
                // которое на мониторе. Окно сразу «под краем» осталось бы со старым DPI (проверено).
                if !icons.is_null() {
                    SetWindowPos(
                        icons,
                        std::ptr::null_mut(),
                        lay.dock.x,
                        lay.dock.y,
                        lay.dock.w,
                        lay.dock.h,
                        SWP_NOACTIVATE | SWP_NOZORDER,
                    );
                    std::thread::sleep(Duration::from_millis(100));
                }
                // Смена DPI тоже гасит Acrylic у подложки — включаем обратно.
                if !backdrop.is_null() {
                    SendMessageW(backdrop, WM_NCACTIVATE, 1, 0);
                }
                eprintln!(
                    "[autohide] layout: monitor {:?}, dock {:?}, scale {}",
                    (
                        lay.monitor.left,
                        lay.monitor.top,
                        lay.monitor.right,
                        lay.monitor.bottom
                    ),
                    lay.dock,
                    lay.scale
                );
            }
            if tick_n % FULLSCREEN_EVERY == 0 {
                let now_fs = notification_busy() || foreground_covers_monitor([backdrop, icons]);
                if now_fs != fullscreen {
                    eprintln!("[autohide] fullscreen {now_fs}");
                    fullscreen = now_fs;
                }
            }
            tick_n = tick_n.wrapping_add(1);

            let mut p = POINT { x: 0, y: 0 };
            GetCursorPos(&mut p);
            let (px, pw) = pill_offset();
            let (left, right) = (lay.dock.x + px, lay.dock.x + px + pw);
            let in_x = p.x >= left && p.x < right;
            let at_edge = in_x && p.y >= lay.monitor.bottom - 2;
            let over_dock = in_x && p.y >= lay.dock.y;
            let shown = state.tick(
                start.elapsed().as_millis() as u64,
                at_edge,
                over_dock,
                fullscreen,
            );

            // Спрятан — окно иконок целиком ниже края монитора.
            let hidden_dy = lay.monitor.bottom - lay.dock.y;
            let dy = ((1.0 - shown) * hidden_dy as f64).round() as i32;
            if dy != last_dy && !icons.is_null() {
                last_dy = dy;
                let y = lay.dock.y + dy;
                // Оба окна одним пакетом — без расхождения подложки и иконок.
                let mut hdwp = BeginDeferWindowPos(2);
                hdwp = DeferWindowPos(
                    hdwp,
                    icons,
                    std::ptr::null_mut(),
                    lay.dock.x,
                    y,
                    lay.dock.w,
                    lay.dock.h,
                    SWP_NOACTIVATE | SWP_NOZORDER,
                );
                if !backdrop.is_null() {
                    let (bx, by) = crate::spike::pill_origin();
                    hdwp = DeferWindowPos(
                        hdwp,
                        backdrop,
                        std::ptr::null_mut(),
                        lay.dock.x + bx,
                        y + by,
                        0,
                        0,
                        SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE,
                    );
                }
                let ok = EndDeferWindowPos(hdwp);
                if std::env::var("WS_TRACE").is_ok() {
                    let mut r: RECT = std::mem::zeroed();
                    GetWindowRect(icons, &mut r);
                    eprintln!(
                        "[autohide] move dy {dy} ok {ok} -> icons ({}, {}) {}x{}",
                        r.left,
                        r.top,
                        r.right - r.left,
                        r.bottom - r.top
                    );
                }
            }

            if wall_mark.elapsed() >= Duration::from_secs(10) {
                let cpu = thread_cpu_ms();
                eprintln!(
                    "[autohide] poll thread CPU {} ms per {} s",
                    cpu - cpu_mark,
                    wall_mark.elapsed().as_secs()
                );
                (cpu_mark, wall_mark) = (cpu, Instant::now());
            }
            std::thread::sleep(TICK);
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn work(left: i32, top: i32, right: i32, bottom: i32) -> RECT {
        RECT {
            left,
            top,
            right,
            bottom,
        }
    }

    #[test]
    fn dock_is_centered_at_bottom_in_physical_pixels() {
        // 1920×1080, 100 %, панель задач 48 px.
        let r = dock_rect(work(0, 0, 1920, 1032), 1.0);
        assert_eq!(
            r,
            Rect {
                x: 360,
                y: 842,
                w: 1200,
                h: 190
            }
        );
        // 2560×1440, 125 %: размер растёт, центр на месте.
        let r = dock_rect(work(0, 0, 2560, 1380), 1.25);
        assert_eq!((r.w, r.h), (1500, 238));
        assert_eq!(r.x + r.w / 2, 1280);
        assert_eq!(r.y + r.h, 1380);
    }

    #[test]
    fn dock_follows_secondary_monitor_with_negative_coordinates() {
        // Второй монитор слева от основного, 150 %.
        let r = dock_rect(work(-2880, 0, 0, 1620), 1.5);
        assert_eq!((r.w, r.h), (1800, 285));
        assert_eq!(r.x, -2880 + (2880 - 1800) / 2);
        assert_eq!(r.y + r.h, 1620);
    }

    #[test]
    fn reveals_only_after_delay_and_hides_after_leaving() {
        let mut a = Autohide::default();
        assert_eq!(a.tick(0, true, false, false), 0.0);
        a.tick(100, true, false, false);
        assert!(!a.shown, "курсор у края 100 мс — рано");
        a.tick(160, true, false, false);
        assert!(a.shown);
        assert_eq!(a.tick(400, false, true, false), 1.0, "выехал полностью");
        a.tick(500, false, false, false);
        assert!(a.shown, "только что ушёл — ещё показан");
        a.tick(950, false, false, false);
        assert!(!a.shown);
    }

    #[test]
    fn flyby_does_not_reveal() {
        let mut a = Autohide::default();
        a.tick(0, true, false, false);
        a.tick(100, false, false, false);
        a.tick(200, true, false, false);
        a.tick(300, true, false, false);
        assert!(!a.shown, "таймер сбрасывается, когда курсор уходит с края");
    }

    #[test]
    fn fullscreen_forces_hidden() {
        let mut a = Autohide::default();
        a.tick(0, true, false, false);
        a.tick(200, true, false, false);
        assert!(a.shown);
        a.tick(216, true, true, true);
        assert!(!a.shown);
        a.tick(600, true, true, true);
        assert!(!a.shown, "у края, но полноэкранное — не показываем");
    }
}
