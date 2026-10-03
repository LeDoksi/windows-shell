//! Окна оболочки (этап 2). Один процесс, у каждой части интерфейса своё окно без рамки:
//! верхняя панель, окно категории, док (подложка + иконки), flyout и поиск.
//! Фронт выбирает, что рисовать, по метке окна (`src/windows/`).

use tauri::{
    AppHandle, Listener, Manager, PhysicalPosition, WebviewUrl, WebviewWindow, WebviewWindowBuilder,
};

use crate::config::{Config, ConfigStore, GlassMode};

pub const LAUNCHER: &str = "launcher";
pub const CATEGORY: &str = "category";
pub const DOCK: &str = "dock";
pub const DOCK_BACKDROP: &str = "dock-backdrop";
pub const LAUNCHER_BACKDROP: &str = "launcher-backdrop";
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

    // Подложки стекла создаём раньше окон с содержимым: так они ниже в z-order.
    let launcher_backdrop = window(app, LAUNCHER_BACKDROP, (1.0, 1.0))?;
    let backdrop = window(app, DOCK_BACKDROP, (1.0, 1.0))?;
    let dock = window(app, DOCK, DOCK_SIZE)?;
    dock.set_position(PhysicalPosition::new(
        centered_x(DOCK_SIZE.0),
        ay + ah - px(DOCK_SIZE.1),
    ))?;

    #[cfg(windows)]
    {
        for w in [
            &launcher,
            &category,
            &search,
            &flyout,
            &launcher_backdrop,
            &backdrop,
            &dock,
        ] {
            crate::dock::tool_window(w);
        }
        crate::dock::setup(&backdrop, &dock, &launcher_backdrop);
    }

    launcher.show()?;
    dock.show()?;
    let glass = app
        .state::<ConfigStore>()
        .0
        .lock()
        .expect("config lock")
        .appearance
        .glass;
    apply_glass(app, glass)?;
    // Режим меняется на лету: конфиг поменяли — показываем или прячем подложки.
    let handle = app.clone();
    app.listen_any("config_changed", move |event| {
        if let Ok(config) = serde_json::from_str::<Config>(event.payload()) {
            if let Err(e) = apply_glass(&handle, config.appearance.glass) {
                eprintln!("[glass] {e}");
            }
        }
    });
    Ok(())
}

/// «Стекло» — подложки с Acrylic видны; «Тонировка» — спрятаны, фон даёт CSS.
fn apply_glass(app: &AppHandle, mode: GlassMode) -> tauri::Result<()> {
    for label in [LAUNCHER_BACKDROP, DOCK_BACKDROP] {
        let Some(w) = app.get_webview_window(label) else {
            continue;
        };
        match mode {
            GlassMode::Acrylic => w.show()?,
            GlassMode::Tint => w.hide()?,
        }
    }
    #[cfg(windows)]
    if mode == GlassMode::Acrylic {
        crate::dock::reactivate_backdrops();
    }
    Ok(())
}

/// Окно сообщает прямоугольник своего «стеклянного» элемента (таблетка дока, верхняя панель)
/// в физических пикселях внутри окна — под ним встаёт подложка `<метка>-backdrop`.
#[tauri::command]
pub fn glass_rect(
    app: AppHandle,
    window: WebviewWindow,
    x: f64,
    y: f64,
    w: f64,
    h: f64,
) -> Result<(), String> {
    let backdrop = app
        .get_webview_window(&format!("{}-backdrop", window.label()))
        .ok_or("no backdrop for this window")?;
    #[cfg(windows)]
    crate::dock::place_backdrop(
        &window,
        &backdrop,
        (
            x.round() as i32,
            y.round() as i32,
            w.round() as i32,
            h.round() as i32,
        ),
    )
    .map_err(|e| e.to_string())?;
    #[cfg(not(windows))]
    let _ = (backdrop, x, y, w, h);
    Ok(())
}
