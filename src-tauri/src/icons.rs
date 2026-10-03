//! Иконки плиток (этап 3): та же картинка, что показывает Проводник, в 256 px.
//! Готовые PNG лежат в папке кэша приложения; ключ — путь и время изменения файла,
//! так что поменявшийся ярлык или обновлённая программа получат новую иконку.

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::UNIX_EPOCH;

use base64::Engine;
use tauri::{AppHandle, Manager, State};

use crate::config::ItemKind;
use crate::launch::is_path;

pub const SIZE: u32 = 256;

/// Иконки, уже отданные окнам в этом запуске: `data:`-URL по ключу кэша.
#[derive(Default)]
pub struct IconCache(Mutex<HashMap<String, Option<String>>>);

/// Что спросить у Проводника: путь или `shell:AppsFolder\<AUMID>`. У ссылок иконки нет.
pub fn source(kind: &ItemKind, target: &str) -> Option<String> {
    let target = target.trim();
    match kind {
        ItemKind::Url => None,
        ItemKind::App if !is_path(target) && !target.is_empty() => {
            Some(format!("shell:AppsFolder\\{target}"))
        }
        _ if Path::new(target).exists() => Some(target.to_string()),
        _ => None,
    }
}

/// Ключ кэша: источник плюс время изменения файла (у приложений из магазина его нет).
pub fn cache_key(source: &str) -> String {
    let modified = fs::metadata(source)
        .and_then(|m| m.modified())
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map_or(0, |d| d.as_secs());
    format!(
        "{:016x}",
        fnv1a(&format!("{}|{modified}", source.to_lowercase()))
    )
}

/// Стабильный между сборками хэш для имени файла в кэше.
fn fnv1a(text: &str) -> u64 {
    text.bytes().fold(0xcbf2_9ce4_8422_2325, |h, b| {
        (h ^ u64::from(b)).wrapping_mul(0x0100_0000_01b3)
    })
}

/// Пиксели Windows (BGRA, альфа умножена на цвет) — в обычный RGBA.
/// У старых иконок без альфа-канала прозрачность нулевая везде: такие делаем непрозрачными.
pub fn bgra_to_rgba(mut pixels: Vec<u8>) -> Vec<u8> {
    let has_alpha = pixels.chunks_exact(4).any(|p| p[3] != 0);
    for p in pixels.chunks_exact_mut(4) {
        p.swap(0, 2);
        if !has_alpha {
            p[3] = 255;
        } else if p[3] != 0 && p[3] != 255 {
            let a = u32::from(p[3]);
            for c in &mut p[..3] {
                *c = ((u32::from(*c) * 255 + a / 2) / a).min(255) as u8;
            }
        }
    }
    pixels
}

pub fn encode_png(width: u32, height: u32, rgba: &[u8]) -> Result<Vec<u8>, String> {
    let mut out = Vec::new();
    let mut encoder = png::Encoder::new(&mut out, width, height);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder.write_header().map_err(|e| e.to_string())?;
    writer.write_image_data(rgba).map_err(|e| e.to_string())?;
    writer.finish().map_err(|e| e.to_string())?;
    Ok(out)
}

fn data_url(png: &[u8]) -> String {
    format!(
        "data:image/png;base64,{}",
        base64::engine::general_purpose::STANDARD.encode(png)
    )
}

/// PNG из кэша на диске или свежий от Проводника (и сразу в кэш).
fn load(dir: &Path, source: &str, key: &str) -> Option<String> {
    let file = dir.join(format!("{key}.png"));
    if let Ok(png) = fs::read(&file) {
        return Some(data_url(&png));
    }
    let png = match os::render(source) {
        Ok(png) => png,
        Err(e) => {
            eprintln!("[icons] {source}: {e}");
            return None;
        }
    };
    if let Err(e) = fs::create_dir_all(dir).and_then(|()| fs::write(&file, &png)) {
        eprintln!("[icons] кэш {}: {e}", file.display());
    }
    Some(data_url(&png))
}

fn cache_dir(app: &AppHandle) -> Option<PathBuf> {
    app.path().app_cache_dir().ok().map(|d| d.join("icons"))
}

/// Иконка плитки как `data:`-URL; `None` — рисуем свою заглушку.
#[tauri::command]
pub async fn item_icon(
    app: AppHandle,
    cache: State<'_, IconCache>,
    kind: ItemKind,
    target: String,
) -> Result<Option<String>, String> {
    let Some(source) = source(&kind, &target) else {
        return Ok(None);
    };
    let key = cache_key(&source);
    if let Some(hit) = cache.0.lock().expect("icon cache").get(&key) {
        return Ok(hit.clone());
    }
    let dir = cache_dir(&app).ok_or("нет папки кэша")?;
    let worker_key = key.clone();
    let icon = tauri::async_runtime::spawn_blocking(move || load(&dir, &source, &worker_key))
        .await
        .map_err(|e| e.to_string())?;
    cache
        .0
        .lock()
        .expect("icon cache")
        .insert(key, icon.clone());
    Ok(icon)
}

#[cfg(windows)]
mod os {
    use windows::core::HSTRING;
    use windows::Win32::Foundation::SIZE;
    use windows::Win32::Graphics::Gdi::{
        CreateCompatibleDC, DeleteDC, DeleteObject, GetDIBits, GetObjectW, BITMAP, BITMAPINFO,
        BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS, HGDIOBJ,
    };
    use windows::Win32::UI::Shell::{
        IShellItemImageFactory, SHCreateItemFromParsingName, SIIGBF_BIGGERSIZEOK, SIIGBF_ICONONLY,
    };

    use super::{bgra_to_rgba, encode_png, SIZE};
    use crate::launch::Com;

    /// Иконка объекта от Проводника — PNG. Для `.lnk` это иконка цели ярлыка.
    pub fn render(source: &str) -> Result<Vec<u8>, String> {
        let _com = Com::init();
        let err = |e: windows::core::Error| e.message();
        unsafe {
            let factory: IShellItemImageFactory =
                SHCreateItemFromParsingName(&HSTRING::from(source), None).map_err(err)?;
            let size = SIZE {
                cx: SIZE as i32,
                cy: SIZE as i32,
            };
            let bitmap = factory
                .GetImage(size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK)
                .map_err(err)?;
            let mut info = BITMAP::default();
            GetObjectW(
                HGDIOBJ(bitmap.0),
                size_of::<BITMAP>() as i32,
                Some(&mut info as *mut _ as *mut _),
            );
            let (w, h) = (info.bmWidth.max(0) as u32, info.bmHeight.unsigned_abs());
            let mut header = BITMAPINFO {
                bmiHeader: BITMAPINFOHEADER {
                    biSize: size_of::<BITMAPINFOHEADER>() as u32,
                    biWidth: w as i32,
                    // Минус — строки сверху вниз, как в PNG.
                    biHeight: -(h as i32),
                    biPlanes: 1,
                    biBitCount: 32,
                    biCompression: BI_RGB.0,
                    ..Default::default()
                },
                ..Default::default()
            };
            let mut pixels = vec![0u8; (w * h * 4) as usize];
            let dc = CreateCompatibleDC(None);
            let lines = GetDIBits(
                dc,
                bitmap,
                0,
                h,
                Some(pixels.as_mut_ptr().cast()),
                &mut header,
                DIB_RGB_COLORS,
            );
            let _ = DeleteDC(dc);
            let _ = DeleteObject(HGDIOBJ(bitmap.0));
            if lines == 0 || w == 0 {
                return Err("пустая картинка".into());
            }
            encode_png(w, h, &bgra_to_rgba(pixels))
        }
    }
}

#[cfg(not(windows))]
mod os {
    pub fn render(_: &str) -> Result<Vec<u8>, String> {
        Err("иконки есть только в Windows".into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn links_have_no_icon_and_apps_go_through_apps_folder() {
        assert_eq!(source(&ItemKind::Url, "https://example.com"), None);
        assert_eq!(
            source(&ItemKind::App, "MSTeams_8wekyb3d8bbwe!MSTeams").as_deref(),
            Some("shell:AppsFolder\\MSTeams_8wekyb3d8bbwe!MSTeams")
        );
        assert_eq!(source(&ItemKind::File, "Z:\\нет\\такого.txt"), None);
    }

    #[test]
    fn cache_key_is_stable_and_case_insensitive() {
        assert_eq!(cache_key("C:\\A\\b.exe"), cache_key("c:\\a\\B.EXE"));
        assert_ne!(cache_key("C:\\a.exe"), cache_key("C:\\b.exe"));
        assert_eq!(fnv1a(""), 0xcbf2_9ce4_8422_2325);
    }

    #[test]
    fn pixels_become_straight_rgba() {
        // Полупрозрачный синий (BGRA, умноженный на альфу) и прозрачный пиксель.
        let rgba = bgra_to_rgba(vec![100, 0, 0, 128, 0, 0, 0, 0]);
        assert_eq!(rgba, vec![0, 0, 199, 128, 0, 0, 0, 0]);
        // Без альфа-канала — непрозрачно.
        assert_eq!(bgra_to_rgba(vec![1, 2, 3, 0]), vec![3, 2, 1, 255]);
    }

    #[test]
    fn png_round_trips() {
        let rgba = vec![10, 20, 30, 255, 40, 50, 60, 128];
        let png = encode_png(2, 1, &rgba).unwrap();
        let decoder = png::Decoder::new(std::io::Cursor::new(png));
        let mut reader = decoder.read_info().unwrap();
        let mut buf = vec![0; reader.output_buffer_size().unwrap()];
        reader.next_frame(&mut buf).unwrap();
        assert_eq!(buf, rgba);
    }
}
