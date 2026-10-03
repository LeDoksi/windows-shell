//! Источники для «＋» (этап 3): системный диалог выбора файлов и папок
//! и список установленных приложений — тот же, что в меню «Пуск».

use serde::Serialize;
use tauri::WebviewWindow;

/// Приложение из «Пуска». `id` — AUMID: запускается через `shell:AppsFolder\<id>`.
#[derive(Debug, Clone, PartialEq, Serialize)]
pub struct InstalledApp {
    pub name: String,
    pub id: String,
}

/// Деинсталляторы и справка в «Пуске» плиткам не нужны.
fn is_noise(name: &str) -> bool {
    let name = name.to_lowercase();
    ["uninstall", "удалить", "удаление", "деинсталл"]
        .iter()
        .any(|word| name.contains(word))
}

/// Без мусора и повторов, по алфавиту.
pub fn tidy(mut apps: Vec<InstalledApp>) -> Vec<InstalledApp> {
    apps.retain(|app| !app.name.trim().is_empty() && !is_noise(&app.name));
    apps.sort_by_cached_key(|app| (app.name.to_lowercase(), app.id.clone()));
    apps.dedup_by(|a, b| a.id == b.id);
    apps
}

/// Системный диалог «Открыть» поверх окна категории. Пустой список — пользователь передумал.
#[tauri::command]
pub async fn pick_paths(window: WebviewWindow, folders: bool) -> Result<Vec<String>, String> {
    #[cfg(windows)]
    let owner = window.hwnd().map(|h| h.0 as isize).unwrap_or_default();
    #[cfg(not(windows))]
    let owner = {
        let _ = window;
        0
    };
    tauri::async_runtime::spawn_blocking(move || os::pick(owner, folders))
        .await
        .map_err(|e| e.to_string())?
}

#[tauri::command]
pub async fn installed_apps() -> Result<Vec<InstalledApp>, String> {
    tauri::async_runtime::spawn_blocking(os::apps)
        .await
        .map_err(|e| e.to_string())?
        .map(tidy)
}

#[cfg(windows)]
mod os {
    use windows::core::{w, PWSTR};
    use windows::Win32::Foundation::{ERROR_CANCELLED, HWND};
    use windows::Win32::System::Com::{CoCreateInstance, CoTaskMemFree, CLSCTX_INPROC_SERVER};
    use windows::Win32::UI::Shell::{
        BHID_EnumItems, FileOpenDialog, IEnumShellItems, IFileOpenDialog, IShellItem,
        SHCreateItemFromParsingName, FOS_ALLOWMULTISELECT, FOS_FORCEFILESYSTEM, FOS_PICKFOLDERS,
        SIGDN, SIGDN_FILESYSPATH, SIGDN_NORMALDISPLAY, SIGDN_PARENTRELATIVEPARSING,
    };

    use super::InstalledApp;
    use crate::launch::Com;

    fn err(e: windows::core::Error) -> String {
        e.message()
    }

    unsafe fn name(item: &IShellItem, kind: SIGDN) -> Result<String, String> {
        let text: PWSTR = item.GetDisplayName(kind).map_err(err)?;
        let result = text.to_string().map_err(|e| e.to_string());
        CoTaskMemFree(Some(text.0 as *const _));
        result
    }

    pub fn pick(owner: isize, folders: bool) -> Result<Vec<String>, String> {
        let _com = Com::init();
        unsafe {
            let dialog: IFileOpenDialog =
                CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER).map_err(err)?;
            let mut options = dialog.GetOptions().map_err(err)? | FOS_FORCEFILESYSTEM;
            options |= if folders {
                FOS_PICKFOLDERS
            } else {
                FOS_ALLOWMULTISELECT
            };
            dialog.SetOptions(options).map_err(err)?;
            let title = if folders {
                w!("Добавить папку")
            } else {
                w!("Добавить файлы")
            };
            dialog.SetTitle(title).map_err(err)?;
            let owner = (owner != 0).then_some(HWND(owner as *mut _));
            if let Err(e) = dialog.Show(owner) {
                return if e.code() == ERROR_CANCELLED.to_hresult() {
                    Ok(Vec::new())
                } else {
                    Err(err(e))
                };
            }
            let results = dialog.GetResults().map_err(err)?;
            (0..results.GetCount().map_err(err)?)
                .map(|i| name(&results.GetItemAt(i).map_err(err)?, SIGDN_FILESYSPATH))
                .collect()
        }
    }

    pub fn apps() -> Result<Vec<InstalledApp>, String> {
        let _com = Com::init();
        let mut apps = Vec::new();
        unsafe {
            let folder: IShellItem =
                SHCreateItemFromParsingName(w!("shell:AppsFolder"), None).map_err(err)?;
            let items: IEnumShellItems =
                folder.BindToHandler(None, &BHID_EnumItems).map_err(err)?;
            loop {
                let mut batch = [None];
                let mut fetched = 0;
                if items.Next(&mut batch, Some(&mut fetched)).is_err() || fetched == 0 {
                    break;
                }
                let Some(item) = batch[0].take() else { break };
                if let (Ok(name), Ok(id)) = (
                    name(&item, SIGDN_NORMALDISPLAY),
                    name(&item, SIGDN_PARENTRELATIVEPARSING),
                ) {
                    apps.push(InstalledApp { name, id });
                }
            }
        }
        Ok(apps)
    }
}

#[cfg(not(windows))]
mod os {
    use super::InstalledApp;

    pub fn pick(_: isize, _: bool) -> Result<Vec<String>, String> {
        Err("диалог выбора есть только в Windows".into())
    }

    pub fn apps() -> Result<Vec<InstalledApp>, String> {
        Ok(Vec::new())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn app(name: &str, id: &str) -> InstalledApp {
        InstalledApp {
            name: name.into(),
            id: id.into(),
        }
    }

    #[test]
    fn tidy_sorts_and_drops_uninstallers_and_duplicates() {
        let apps = tidy(vec![
            app("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App"),
            app("Uninstall Foo", "{6D809377}\\Foo\\uninstall.exe"),
            app(
                "калькулятор",
                "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            ),
            app("Paint", "Microsoft.Paint_8wekyb3d8bbwe!App"),
            app("Excel", "Microsoft.Office.EXCEL.EXE.15"),
            app(" ", "x"),
        ]);
        let names: Vec<_> = apps.iter().map(|a| a.name.as_str()).collect();
        assert_eq!(names, ["Excel", "Paint", "калькулятор"]);
    }
}
