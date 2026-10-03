//! Полосы оболочки — верхняя панель и док: автоскрытие, закрепление, AppBar (WSH-16).
//! Механика из спайка WSH-6 (`spike_autohide.rs`): поток опрашивает курсор ~60 раз в секунду,
//! полоса выезжает, если курсор у края дольше 150 мс, и уезжает через 400 мс после ухода.
//! Закреплённая полоса видна всегда и резервирует место через AppBar, чтобы развёрнутые окна
//! её не перекрывали. Над полноэкранными приложениями полосы прячутся всегда.
//! Монитор — из конфига (`bars.monitor`, WSH-17); нет такого — основной. Мониторы опрашиваем
//! дважды в секунду: подключение, разрешение, масштаб и панель задач ловятся без подписок.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

use tauri::{AppHandle, Emitter, Listener, Manager};
use windows_sys::core::BOOL;
use windows_sys::Win32::Foundation::{HWND, LPARAM, POINT, RECT};
use windows_sys::Win32::Graphics::Gdi::{
    EnumDisplayMonitors, GetMonitorInfoW, MonitorFromPoint, MonitorFromWindow, HDC, HMONITOR,
    MONITORINFO, MONITORINFOEXW, MONITOR_DEFAULTTONEAREST, MONITOR_DEFAULTTOPRIMARY,
};
use windows_sys::Win32::UI::HiDpi::{
    GetDpiForMonitor, SetThreadDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
    MDT_EFFECTIVE_DPI,
};
use windows_sys::Win32::UI::Shell::{
    SHAppBarMessage, SHQueryUserNotificationState, ABE_BOTTOM, ABE_TOP, ABM_NEW, ABM_QUERYPOS,
    ABM_REMOVE, ABM_SETPOS, APPBARDATA,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    BeginDeferWindowPos, DeferWindowPos, EndDeferWindowPos, GetClassNameW, GetCursorPos,
    GetForegroundWindow, GetWindowRect, GetWindowThreadProcessId, IsWindowVisible, SWP_NOACTIVATE,
    SWP_NOSIZE, SWP_NOZORDER,
};

use crate::config::{Bars, Config, ConfigStore};
use crate::shell::{self, CATEGORY, DOCK, FLYOUT, LAUNCHER, SEARCH};

/// Курсор у края дольше этого — показываем (чтобы полоса не выскакивала, когда курсор пролетает).
pub const REVEAL_DELAY_MS: u64 = 150;
/// Курсор ушёл с полосы дольше этого — прячем.
pub const HIDE_DELAY_MS: u64 = 400;
/// Длительность выезда/уезда.
pub const SLIDE_MS: f64 = 180.0;
const TICK: Duration = Duration::from_millis(16);
/// Раскладку мониторов и полноэкранный режим проверяем реже, чем курсор.
const LAYOUT_EVERY: u32 = 30;
const FULLSCREEN_EVERY: u32 = 15;

/// Закреплены ли панель и док (из конфига).
static PINNED: [AtomicBool; 2] = [AtomicBool::new(false), AtomicBool::new(false)];
/// Закрепление поменялось — пересчитать AppBar.
static APPBAR_DIRTY: AtomicBool = AtomicBool::new(true);
/// Выбранный монитор (имя устройства, `\\.\DISPLAY2`); `None` — основной.
static MONITOR: Mutex<Option<String>> = Mutex::new(None);
/// Выбор монитора поменялся — пересчитать раскладку, не дожидаясь опроса.
static LAYOUT_DIRTY: AtomicBool = AtomicBool::new(true);

fn apply(bars: &Bars) {
    PINNED[0].store(bars.launcher_pinned, Ordering::Relaxed);
    PINNED[1].store(bars.dock_pinned, Ordering::Relaxed);
    APPBAR_DIRTY.store(true, Ordering::Relaxed);
    *MONITOR.lock().expect("monitor lock") = bars.monitor.clone();
    LAYOUT_DIRTY.store(true, Ordering::Relaxed);
}

fn ease_out(t: f64) -> f64 {
    1.0 - (1.0 - t).powi(3)
}

/// Состояние автоскрытия одной полосы. `progress`: 0 — спрятана, 1 — показана.
#[derive(Debug, Default)]
pub struct Autohide {
    pub shown: bool,
    edge_since: Option<u64>,
    away_since: Option<u64>,
    progress: f64,
    last: Option<u64>,
}

/// Что видит полоса на этом шаге.
#[derive(Debug, Default, Clone, Copy)]
pub struct Input {
    /// Курсор у края экрана в пределах полосы.
    pub at_edge: bool,
    /// Курсор над полосой или открыт её поповер (категория, поиск, flyout).
    pub engaged: bool,
    pub pinned: bool,
    pub fullscreen: bool,
}

impl Autohide {
    /// Шаг по времени `now` (мс). Возвращает долю видимости с easing: 0..1.
    pub fn tick(&mut self, now: u64, i: Input) -> f64 {
        if i.fullscreen {
            self.shown = false;
            self.edge_since = None;
        } else if i.pinned {
            self.shown = true;
            self.away_since = None;
        } else if self.shown {
            if i.engaged {
                self.away_since = None;
            } else if now - *self.away_since.get_or_insert(now) >= HIDE_DELAY_MS {
                self.shown = false;
                self.away_since = None;
            }
        } else if i.at_edge {
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

#[derive(Clone, Copy, PartialEq)]
enum Edge {
    Top,
    Bottom,
}

struct Bar {
    label: &'static str,
    edge: Edge,
    owner: HWND,
    backdrop: HWND,
    state: Autohide,
    /// Прямоугольник, зарезервированный AppBar (если полоса закреплена).
    appbar: Option<RECT>,
}

fn rect_of(h: HWND) -> RECT {
    let mut r: RECT = unsafe { std::mem::zeroed() };
    unsafe { GetWindowRect(h, &mut r) };
    r
}

/// Монитор в физических пикселях.
#[derive(Clone)]
pub struct Layout {
    pub name: String,
    pub monitor: RECT,
    pub work: RECT,
    pub scale: f64,
}

/// Тот же монитор с тем же размером и масштабом. Рабочую область не сравниваем: она меняется
/// от наших же AppBar и от панели задач, а переставлять окна из-за этого не нужно.
impl PartialEq for Layout {
    fn eq(&self, o: &Self) -> bool {
        let key = |l: &Layout| {
            let m = l.monitor;
            ([m.left, m.top, m.right, m.bottom], l.scale)
        };
        self.name == o.name && key(self) == key(o)
    }
}

fn monitor_layout(mon: HMONITOR) -> Layout {
    let mut info: MONITORINFOEXW = unsafe { std::mem::zeroed() };
    info.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;
    let (mut dx, mut dy) = (96, 96);
    unsafe {
        GetMonitorInfoW(mon, &mut info as *mut _ as *mut MONITORINFO);
        GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, &mut dx, &mut dy);
    }
    let n = info.szDevice.iter().position(|&c| c == 0).unwrap_or(0);
    Layout {
        name: String::from_utf16_lossy(&info.szDevice[..n]),
        monitor: info.monitorInfo.rcMonitor,
        work: info.monitorInfo.rcWork,
        scale: dx as f64 / 96.0,
    }
}

/// Все мониторы; основной — первый.
pub fn monitors() -> Vec<Layout> {
    unsafe extern "system" fn push(m: HMONITOR, _: HDC, _: *mut RECT, out: LPARAM) -> BOOL {
        (*(out as *mut Vec<HMONITOR>)).push(m);
        1
    }
    let mut all: Vec<HMONITOR> = Vec::new();
    unsafe {
        EnumDisplayMonitors(
            std::ptr::null_mut(),
            std::ptr::null(),
            Some(push),
            &mut all as *mut _ as LPARAM,
        )
    };
    let primary = unsafe { MonitorFromPoint(POINT { x: 0, y: 0 }, MONITOR_DEFAULTTOPRIMARY) };
    all.sort_by_key(|&m| m != primary);
    all.into_iter().map(monitor_layout).collect()
}

/// Выбранный монитор, если он подключён, иначе основной (первый).
pub fn pick(all: &[Layout], choice: Option<&str>) -> Option<Layout> {
    choice
        .and_then(|c| all.iter().find(|l| l.name == c))
        .or(all.first())
        .cloned()
}

fn is_ours(h: HWND) -> bool {
    let mut pid = 0;
    unsafe { GetWindowThreadProcessId(h, &mut pid) };
    pid == std::process::id()
}

/// Полноэкранное приложение: Windows говорит «занят/игра/презентация» или активное чужое окно
/// закрывает свой монитор (рабочий стол и панель задач не считаются).
fn fullscreen() -> bool {
    let mut quns = 0;
    unsafe { SHQueryUserNotificationState(&mut quns) };
    if matches!(quns, 2..=4) {
        return true;
    }
    let fg = unsafe { GetForegroundWindow() };
    if fg.is_null() || is_ours(fg) {
        return false;
    }
    let mut class = [0u16; 64];
    let n = unsafe { GetClassNameW(fg, class.as_mut_ptr(), class.len() as i32) } as usize;
    if matches!(
        String::from_utf16_lossy(&class[..n]).as_str(),
        "Progman" | "WorkerW" | "Shell_TrayWnd"
    ) {
        return false;
    }
    let r = rect_of(fg);
    let mut info: MONITORINFO = unsafe { std::mem::zeroed() };
    info.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
    unsafe { GetMonitorInfoW(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), &mut info) };
    let m = info.rcMonitor;
    r.left <= m.left && r.top <= m.top && r.right >= m.right && r.bottom >= m.bottom
}

fn appbar_data(h: HWND, edge: Edge, rc: RECT) -> APPBARDATA {
    let mut d: APPBARDATA = unsafe { std::mem::zeroed() };
    d.cbSize = std::mem::size_of::<APPBARDATA>() as u32;
    d.hWnd = h;
    d.uEdge = if edge == Edge::Top {
        ABE_TOP
    } else {
        ABE_BOTTOM
    };
    d.rc = rc;
    d
}

/// Зарезервировать полосу высотой `reserve` у края монитора. Windows может сдвинуть её
/// (например, док встанет над системной панелью задач) — возвращаем итоговый прямоугольник.
fn appbar_set(h: HWND, edge: Edge, monitor: RECT, reserve: i32, registered: bool) -> RECT {
    let mut d = appbar_data(h, edge, monitor);
    unsafe {
        if !registered {
            SHAppBarMessage(ABM_NEW, &mut d);
        }
        match edge {
            Edge::Top => d.rc.bottom = d.rc.top + reserve,
            Edge::Bottom => d.rc.top = d.rc.bottom - reserve,
        }
        SHAppBarMessage(ABM_QUERYPOS, &mut d);
        match edge {
            Edge::Top => d.rc.bottom = d.rc.top + reserve,
            Edge::Bottom => d.rc.top = d.rc.bottom - reserve,
        }
        SHAppBarMessage(ABM_SETPOS, &mut d);
    }
    d.rc
}

fn appbar_remove(h: HWND) {
    let mut d = appbar_data(h, Edge::Top, unsafe { std::mem::zeroed() });
    unsafe { SHAppBarMessage(ABM_REMOVE, &mut d) };
}

/// Сколько резервировать: от края экрана до дальнего края «стеклянного» элемента + 8 px.
fn reserve(bar: &Bar, height: i32, scale: f64) -> i32 {
    let gap = (8.0 * scale).round() as i32;
    match (shell::glass_of(bar.label), bar.edge) {
        (Some((_, y, _, h)), Edge::Top) => y + h + gap,
        (Some((_, y, _, _)), Edge::Bottom) => height - y + gap,
        (None, _) => height,
    }
}

/// Убрать все AppBar при выходе: иначе зарезервированное место останется у Explorer.
pub fn shutdown(app: &AppHandle) {
    for label in [LAUNCHER, DOCK] {
        if let Some(w) = app.get_webview_window(label) {
            if let Ok(h) = w.hwnd() {
                appbar_remove(h.0);
            }
        }
    }
}

pub fn start(app: &AppHandle) {
    apply(
        &app.state::<ConfigStore>()
            .0
            .lock()
            .expect("config lock")
            .bars,
    );
    app.listen_any("config_changed", |event| {
        if let Ok(config) = serde_json::from_str::<Config>(event.payload()) {
            apply(&config.bars);
        }
    });
    let hwnd_of = |label: &str| {
        app.get_webview_window(label)
            .and_then(|w| w.hwnd().ok())
            .map_or(std::ptr::null_mut(), |h| h.0)
    };
    let bar = |label, edge, backdrop| Bar {
        label,
        edge,
        owner: hwnd_of(label),
        backdrop: hwnd_of(backdrop),
        state: Autohide::default(),
        appbar: None,
    };
    // Поповеры держат свою полосу показанной: категория и поиск — панель, flyout — док.
    let bars = vec![
        (
            bar(LAUNCHER, Edge::Top, shell::LAUNCHER_BACKDROP),
            [hwnd_of(CATEGORY), hwnd_of(SEARCH)],
        ),
        (
            bar(DOCK, Edge::Bottom, shell::DOCK_BACKDROP),
            [hwnd_of(FLYOUT), std::ptr::null_mut()],
        ),
    ];
    for (bar, _) in &bars {
        if bar.owner.is_null() {
            eprintln!("[bars] no window {}", bar.label);
        }
    }
    let owned = SendBars(bars);
    let app = app.clone();

    std::thread::spawn(move || {
        let mut bars = owned.into_inner();
        unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
        let start = Instant::now();
        let mut all = Vec::new();
        let mut lay: Option<Layout> = None;
        let mut full = false;
        let mut tick = 0u32;
        loop {
            if LAYOUT_DIRTY.swap(false, Ordering::Relaxed) || tick % LAYOUT_EVERY == 0 {
                let now = monitors();
                if now != all {
                    all = now;
                    // Меню трея перечисляет мониторы — пусть перестроится.
                    let _ = app.emit("monitors_changed", ());
                }
                let choice = MONITOR.lock().expect("monitor lock").clone();
                let new = pick(&all, choice.as_deref());
                if new != lay {
                    if let Some(new) = &new {
                        relayout(&app, &bars, lay.as_ref(), new);
                    }
                }
                lay = new;
            }
            let Some(Layout {
                monitor,
                work,
                scale,
                ..
            }) = lay
            else {
                tick = tick.wrapping_add(1);
                std::thread::sleep(TICK);
                continue;
            };
            if tick % FULLSCREEN_EVERY == 0 {
                full = fullscreen();
            }
            tick = tick.wrapping_add(1);
            let mut p = POINT { x: 0, y: 0 };
            unsafe { GetCursorPos(&mut p) };
            let now = start.elapsed().as_millis() as u64;
            let dirty = APPBAR_DIRTY.swap(false, Ordering::Relaxed);

            for (i, (bar, popover)) in bars.iter_mut().enumerate() {
                if bar.owner.is_null() {
                    continue;
                }
                let r = rect_of(bar.owner);
                let (w, h) = (r.right - r.left, r.bottom - r.top);
                let pinned = PINNED[i].load(Ordering::Relaxed);

                if dirty {
                    bar.appbar = match (pinned, bar.appbar) {
                        (true, registered) => Some(appbar_set(
                            bar.owner,
                            bar.edge,
                            monitor,
                            reserve(bar, h, scale),
                            registered.is_some(),
                        )),
                        (false, Some(_)) => {
                            appbar_remove(bar.owner);
                            None
                        }
                        (false, None) => None,
                    };
                }

                // Место полосы в показанном состоянии и смещение, на котором её не видно.
                let x = monitor.left + (monitor.right - monitor.left - w) / 2;
                let (base_y, hidden_dy) = match bar.edge {
                    Edge::Top => {
                        let y = bar.appbar.map_or(monitor.top, |a| a.top);
                        (y, monitor.top - (y + h))
                    }
                    Edge::Bottom => {
                        let y = bar.appbar.map_or(work.bottom, |a| a.bottom) - h;
                        (y, monitor.bottom - y)
                    }
                };

                let in_x = p.x >= x && p.x < x + w;
                let at_edge = in_x
                    && match bar.edge {
                        Edge::Top => p.y <= monitor.top + 1,
                        Edge::Bottom => p.y >= monitor.bottom - 2,
                    };
                let over = in_x
                    && match bar.edge {
                        Edge::Top => p.y < base_y + h,
                        Edge::Bottom => p.y >= base_y,
                    };
                let popover_open = popover
                    .iter()
                    .any(|&h| !h.is_null() && unsafe { IsWindowVisible(h) } != 0);
                let shown = bar.state.tick(
                    now,
                    Input {
                        at_edge,
                        engaged: over || popover_open,
                        pinned,
                        fullscreen: full,
                    },
                );
                let pos = (
                    x,
                    base_y + ((1.0 - shown) * hidden_dy as f64).round() as i32,
                );
                // Сравниваем с фактическим местом: фронт двигает панель, подгоняя её по ширине.
                if (r.left, r.top) != pos {
                    move_bar(bar, pos);
                }
            }
            std::thread::sleep(TICK);
        }
    });
}

/// Поменялись монитор или его масштаб. Windows шлёт `WM_DPICHANGED` только окну, которое
/// переезжает с монитора на монитор, а спрятанная полоса — за краем, ни на каком мониторе
/// (спайк WSH-6, подвох 1; проверено: без этого панель осталась в масштабе 175 % на 100 %).
/// Поэтому полосы сначала выставляем на старом мониторе, потом на новом, — Tauri пересчитает
/// размер. Дальше поток опроса снова спрячет их, если надо.
fn relayout(app: &AppHandle, bars: &[(Bar, [HWND; 2])], from: Option<&Layout>, lay: &Layout) {
    eprintln!("[bars] monitor {} scale {}", lay.name, lay.scale);
    if let Some(from) = from {
        place_shown(bars, from);
        std::thread::sleep(Duration::from_millis(50));
    }
    place_shown(bars, lay);
    std::thread::sleep(Duration::from_millis(100));
    APPBAR_DIRTY.store(true, Ordering::Relaxed);
    let m = lay.monitor;
    shell::place_popovers(
        app,
        (m.left, m.top, m.right - m.left, m.bottom - m.top),
        lay.scale,
    );
    crate::dock::reactivate_backdrops();
}

/// Полосы — в показанное место у краёв рабочей области монитора.
fn place_shown(bars: &[(Bar, [HWND; 2])], lay: &Layout) {
    for (bar, _) in bars {
        if bar.owner.is_null() {
            continue;
        }
        let r = rect_of(bar.owner);
        let (w, h) = (r.right - r.left, r.bottom - r.top);
        let x = lay.work.left + (lay.work.right - lay.work.left - w) / 2;
        let y = match bar.edge {
            Edge::Top => lay.work.top,
            Edge::Bottom => lay.work.bottom - h,
        };
        move_bar(bar, (x, y));
    }
}

/// Хэндлы окон из главного потока в поток опроса.
struct SendBars(Vec<(Bar, [HWND; 2])>);
unsafe impl Send for SendBars {}
impl SendBars {
    /// Через метод: иначе замыкание захватит поле `.0`, а не обёртку.
    fn into_inner(self) -> Vec<(Bar, [HWND; 2])> {
        self.0
    }
}

/// Сдвинуть окно полосы и её подложку одним пакетом — без расхождения.
fn move_bar(bar: &Bar, (x, y): (i32, i32)) {
    unsafe {
        let mut hdwp = BeginDeferWindowPos(2);
        hdwp = DeferWindowPos(
            hdwp,
            bar.owner,
            std::ptr::null_mut(),
            x,
            y,
            0,
            0,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE,
        );
        if let (false, Some((gx, gy, _, _))) = (bar.backdrop.is_null(), shell::glass_of(bar.label))
        {
            hdwp = DeferWindowPos(
                hdwp,
                bar.backdrop,
                std::ptr::null_mut(),
                x + gx,
                y + gy,
                0,
                0,
                SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOSIZE,
            );
        }
        EndDeferWindowPos(hdwp);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(now: u64, a: &mut Autohide, i: Input) -> bool {
        a.tick(now, i);
        a.shown
    }

    const EDGE: Input = Input {
        at_edge: true,
        engaged: false,
        pinned: false,
        fullscreen: false,
    };
    const AWAY: Input = Input {
        at_edge: false,
        engaged: false,
        pinned: false,
        fullscreen: false,
    };

    #[test]
    fn reveals_after_delay_and_hides_after_leaving() {
        let mut a = Autohide::default();
        assert!(!at(0, &mut a, EDGE));
        assert!(!at(100, &mut a, EDGE), "у края 100 мс — рано");
        assert!(at(160, &mut a, EDGE));
        assert!(at(500, &mut a, AWAY), "только что ушёл — ещё показана");
        assert!(!at(950, &mut a, AWAY));
    }

    #[test]
    fn flyby_does_not_reveal() {
        let mut a = Autohide::default();
        at(0, &mut a, EDGE);
        at(100, &mut a, AWAY);
        at(200, &mut a, EDGE);
        assert!(
            !at(300, &mut a, EDGE),
            "таймер сбрасывается, когда курсор уходит с края"
        );
    }

    #[test]
    fn open_popover_keeps_bar_shown() {
        let mut a = Autohide::default();
        at(0, &mut a, EDGE);
        at(200, &mut a, EDGE);
        let popover = Input {
            engaged: true,
            ..AWAY
        };
        assert!(at(5000, &mut a, popover), "поповер открыт — не прячем");
    }

    #[test]
    fn pinned_is_always_shown_except_fullscreen() {
        let mut a = Autohide::default();
        let pinned = Input {
            pinned: true,
            ..AWAY
        };
        assert!(at(0, &mut a, pinned));
        assert_eq!(a.tick(400, pinned), 1.0, "выехала полностью");
        assert!(!at(
            500,
            &mut a,
            Input {
                fullscreen: true,
                ..pinned
            }
        ));
        assert!(
            at(600, &mut a, pinned),
            "после полноэкранного — снова видна"
        );
    }

    fn mon(name: &str) -> Layout {
        Layout {
            name: name.into(),
            monitor: RECT {
                left: 0,
                top: 0,
                right: 1,
                bottom: 1,
            },
            work: RECT {
                left: 0,
                top: 0,
                right: 1,
                bottom: 1,
            },
            scale: 1.0,
        }
    }

    #[test]
    fn picks_chosen_monitor_or_falls_back_to_primary() {
        let all = [mon("primary"), mon("second")];
        assert_eq!(pick(&all, Some("second")).unwrap().name, "second");
        assert_eq!(pick(&all, None).unwrap().name, "primary");
        assert_eq!(
            pick(&all, Some("unplugged")).unwrap().name,
            "primary",
            "отключён — основной"
        );
        assert!(pick(&[], Some("second")).is_none());
    }
}
