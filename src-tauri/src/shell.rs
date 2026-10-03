//! Окна оболочки (этап 2). Один процесс, у каждой части интерфейса своё окно без рамки:
//! верхняя панель, окно категории, док (подложка + иконки), flyout и поиск.
//! Фронт выбирает, что рисовать, по метке окна (`src/windows/`).

use tauri::{
    AppHandle, Manager, PhysicalPosition, WebviewUrl, WebviewWindow, WebviewWindowBuilder,
};

pub const LAUNCHER: &str = "launcher";
pub const CATEGORY: &str = "category";
pub const DOCK: &str = "dock";
pub const DOCK_BACKDROP: &str = "dock-backdrop";
pub const FLYOUT: &str = "flyout";
pub const SEARCH: &str = "search";

/// Размеры окон в логических пикселях.
const LAUNCHER_H: f64 = 80.0;
const CATEGORY_SIZE: (f64, f64) = (680.0, 620.0);
/// Окно категории — под панелью, с отступом под саму панель.
const CATEGORY_TOP: f64 = 66.0;
const SEARCH_SIZE: (f64, f64) = (640.0, 440.0);
const FLYOUT_SIZE: (f64, f64) = (380.0, 560.0);
/// Окно иконок дока выше таблетки: место под увеличение.
const DOCK_SIZE: (f64, f64) = (1200.0, 190.0);

fn window(app: &AppHandle, label: &str, (w, h): (f64, f64)) -> tauri::Result<WebviewWindow> {
    WebviewWindowBuilder::new(app, label, WebviewUrl::App("index.html".into()))
        .title("Windows Shell")
        .inner_size(w, h)
        .decorations(false)
        .transparent(true)
        .shadow(false)
        .resizable(false)
        .skip_taskbar(true)
        .always_on_top(true)
        .focused(false)
        .visible(false)
        .build()
}

/// Создать все окна на основном мониторе. Видимы сразу только панель и док.
pub fn create(app: &AppHandle) -> tauri::Result<()> {
    let monitor = app.primary_monitor()?.expect("primary monitor");
    let s = monitor.scale_factor();
    let area = monitor.work_area();
    let (ax, ay) = (area.position.x, area.position.y);
    let (aw, ah) = (area.size.width as i32, area.size.height as i32);
    let px = |v: f64| (v * s).round() as i32;
    let centered_x = |w: f64| ax + (aw - px(w)) / 2;

    let launcher = window(app, LAUNCHER, (aw as f64 / s, LAUNCHER_H))?;
    launcher.set_position(PhysicalPosition::new(ax, ay))?;

    let category = window(app, CATEGORY, CATEGORY_SIZE)?;
    category.set_position(PhysicalPosition::new(
        centered_x(CATEGORY_SIZE.0),
        ay + px(CATEGORY_TOP),
    ))?;

    let search = window(app, SEARCH, SEARCH_SIZE)?;
    search.set_position(PhysicalPosition::new(
        centered_x(SEARCH_SIZE.0),
        ay + (ah - px(SEARCH_SIZE.1)) / 3,
    ))?;

    // Flyout ставит на место сам док: над кнопкой системного хвоста.
    let flyout = window(app, FLYOUT, FLYOUT_SIZE)?;

    let backdrop = window(app, DOCK_BACKDROP, (1.0, 1.0))?;
    let dock = window(app, DOCK, DOCK_SIZE)?;
    dock.set_position(PhysicalPosition::new(
        centered_x(DOCK_SIZE.0),
        ay + ah - px(DOCK_SIZE.1),
    ))?;

    #[cfg(windows)]
    {
        for w in [&launcher, &category, &search, &flyout, &backdrop, &dock] {
            crate::dock::tool_window(w);
        }
        crate::dock::setup(&backdrop, &dock);
    }

    launcher.show()?;
    backdrop.show()?;
    dock.show()?;
    Ok(())
}

/// Окно иконок дока сообщает прямоугольник таблетки (физические пиксели внутри окна).
#[tauri::command]
pub fn dock_pill(app: AppHandle, x: f64, y: f64, w: f64, h: f64) -> Result<(), String> {
    let icons = app.get_webview_window(DOCK).ok_or("no dock window")?;
    #[cfg(windows)]
    crate::dock::place_backdrop(
        &icons,
        x.round() as i32,
        y.round() as i32,
        w.round() as i32,
        h.round() as i32,
    )
    .map_err(|e| e.to_string())?;
    #[cfg(not(windows))]
    let _ = (icons, x, y, w, h);
    Ok(())
}
