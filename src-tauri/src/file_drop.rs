//! Файлы из Проводника (этап 6). Перетаскивание принимает сам WebView2, как обычный браузер:
//! страница видит `dragover`/`drop`, а пути на диске отдаёт через
//! `chrome.webview.postMessageWithAdditionalObjects` — сюда, а отсюда событием `files_dropped`
//! обратно в то же окно.
//!
//! Встроенный приём файлов Tauri (wry) не годится: он ставит свой IDropTarget на окна WebView2
//! один раз при создании, и окно браузерного процесса под курсором то попадает под него, то нет
//! (гонка с самим WebView2) — тогда файлы из Проводника не принимаются вовсе (тест WSH-E7).

use serde::Serialize;
use tauri::WebviewWindow;

/// Ответ на бросание: `id` — номер из сообщения страницы, пути — в том же порядке.
#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct FilesDropped {
    pub id: u64,
    pub paths: Vec<String>,
}

/// Номер бросания из сообщения страницы `{"wshDrop": id}`; чужие сообщения — `None`.
pub fn drop_id(json: &str) -> Option<u64> {
    serde_json::from_str::<serde_json::Value>(json)
        .ok()?
        .get("wshDrop")?
        .as_u64()
}

/// Слушать сообщения страницы с файлами в этом окне.
pub fn attach(window: &WebviewWindow) {
    #[cfg(windows)]
    os::attach(window);
    #[cfg(not(windows))]
    let _ = window;
}

#[cfg(windows)]
mod os {
    use tauri::{Emitter, Manager, WebviewWindow};
    use webview2_com::Microsoft::Web::WebView2::Win32::{
        ICoreWebView2File, ICoreWebView2FileSystemHandle, ICoreWebView2WebMessageReceivedEventArgs,
        ICoreWebView2WebMessageReceivedEventArgs2,
    };
    use webview2_com::{take_pwstr, WebMessageReceivedEventHandler};
    use windows::core::{Interface, PWSTR};

    use super::{drop_id, FilesDropped};

    pub fn attach(window: &WebviewWindow) {
        let app = window.app_handle().clone();
        let label = window.label().to_string();
        let result = window.with_webview(move |webview| unsafe {
            let Ok(core) = webview.controller().CoreWebView2() else {
                return;
            };
            let handler = WebMessageReceivedEventHandler::create(Box::new(move |_, args| {
                if let Some(dropped) = args.and_then(|a| files(&a)) {
                    if let Err(e) = app.emit_to(label.as_str(), "files_dropped", dropped) {
                        eprintln!("[file_drop] {e}");
                    }
                }
                Ok(())
            }));
            let mut token = 0;
            if let Err(e) = core.add_WebMessageReceived(&handler, &mut token) {
                eprintln!("[file_drop] {e}");
            }
        });
        if let Err(e) = result {
            eprintln!("[file_drop] {e}");
        }
    }

    /// Пути из сообщения страницы: `File` (файл) или `FileSystemHandle` (файл или папка).
    unsafe fn files(args: &ICoreWebView2WebMessageReceivedEventArgs) -> Option<FilesDropped> {
        let mut json = PWSTR::null();
        args.WebMessageAsJson(&mut json).ok()?;
        let id = drop_id(&take_pwstr(json))?;
        let objects = args
            .cast::<ICoreWebView2WebMessageReceivedEventArgs2>()
            .ok()?
            .AdditionalObjects()
            .ok()?;
        let mut count = 0;
        objects.Count(&mut count).ok()?;
        let paths = (0..count)
            .filter_map(|i| {
                let object = objects.GetValueAtIndex(i).ok()?;
                let mut path = PWSTR::null();
                if let Ok(file) = object.cast::<ICoreWebView2File>() {
                    file.Path(&mut path).ok()?;
                } else {
                    object
                        .cast::<ICoreWebView2FileSystemHandle>()
                        .ok()?
                        .Path(&mut path)
                        .ok()?;
                }
                Some(take_pwstr(path))
            })
            .collect();
        Some(FilesDropped { id, paths })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_drop_messages_carry_an_id() {
        assert_eq!(drop_id(r#"{"wshDrop":42}"#), Some(42));
        assert_eq!(drop_id(r#"{"cmd":"config_get"}"#), None);
        assert_eq!(drop_id("\"text\""), None);
        assert_eq!(drop_id("not json"), None);
    }
}
