//! Запуск плиток (этап 3): файлы, папки, ярлыки `.lnk`, exe, UWP-приложения и ссылки.
//! Всё открывает оболочка Windows (`ShellExecuteEx`), как двойной клик в Проводнике:
//! программа по умолчанию, ярлыки, «Открыть с помощью» решает система, а не мы.

use std::path::Path;

use crate::config::ItemKind;

/// Что именно отдать `ShellExecuteEx`.
#[derive(Debug, PartialEq)]
pub struct Plan {
    pub file: String,
    /// Рабочая папка: для файла и программы — папка, где они лежат.
    pub dir: Option<String>,
}

/// Путь на диске, а не AUMID (`Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`) и не ссылка.
pub fn is_path(target: &str) -> bool {
    target.contains('\\') || target.contains('/')
}

fn existing(target: &str) -> Result<&Path, String> {
    let path = Path::new(target);
    if path.exists() {
        Ok(path)
    } else {
        Err(format!("не найдено: {target}"))
    }
}

fn parent(path: &Path) -> Option<String> {
    path.parent()
        .filter(|p| !p.as_os_str().is_empty())
        .map(|p| p.to_string_lossy().into_owned())
}

pub fn plan(kind: &ItemKind, target: &str) -> Result<Plan, String> {
    let target = target.trim();
    if target.is_empty() {
        return Err("пустой адрес".into());
    }
    match kind {
        ItemKind::Url => {
            if !target.contains(':') {
                return Err(format!("не ссылка: {target}"));
            }
            Ok(Plan {
                file: target.into(),
                dir: None,
            })
        }
        ItemKind::Folder => Ok(Plan {
            file: existing(target)?.to_string_lossy().into_owned(),
            dir: None,
        }),
        ItemKind::App if !is_path(target) => Ok(Plan {
            file: format!("shell:AppsFolder\\{target}"),
            dir: None,
        }),
        ItemKind::File | ItemKind::App => {
            let path = existing(target)?;
            Ok(Plan {
                file: path.to_string_lossy().into_owned(),
                dir: parent(path),
            })
        }
    }
}

/// Открыть объект плитки. Работает в фоновом потоке: сетевой диск может думать секунды,
/// а интерфейс в это время не должен замирать.
#[tauri::command]
pub async fn launch(kind: ItemKind, target: String) -> Result<(), String> {
    let plan = plan(&kind, &target)?;
    tauri::async_runtime::spawn_blocking(move || os::open(&plan))
        .await
        .map_err(|e| e.to_string())?
}

/// Открыть папку объекта в Проводнике и выделить его.
#[tauri::command]
pub async fn reveal(target: String) -> Result<(), String> {
    let path = existing(target.trim())?.to_path_buf();
    tauri::async_runtime::spawn_blocking(move || os::reveal(&path))
        .await
        .map_err(|e| e.to_string())?
}

/// Какие пути из списка есть на диске: недоступные плитки рисуем приглушёнными.
#[tauri::command]
pub async fn paths_exist(paths: Vec<String>) -> Vec<bool> {
    tauri::async_runtime::spawn_blocking(move || {
        paths.iter().map(|p| Path::new(p.trim()).exists()).collect()
    })
    .await
    .unwrap_or_default()
}

#[cfg(windows)]
mod os {
    use std::os::windows::ffi::OsStrExt;
    use std::path::Path;
    use std::ptr::{null, null_mut};

    use windows_sys::Win32::System::Com::{
        CoInitializeEx, CoUninitialize, COINIT_APARTMENTTHREADED, COINIT_DISABLE_OLE1DDE,
    };
    use windows_sys::Win32::UI::Shell::{
        ILCreateFromPathW, ILFree, SHOpenFolderAndSelectItems, ShellExecuteExW,
        SEE_MASK_FLAG_NO_UI, SEE_MASK_NOASYNC, SHELLEXECUTEINFOW,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

    use super::Plan;

    fn wide(text: impl AsRef<std::ffi::OsStr>) -> Vec<u16> {
        text.as_ref().encode_wide().chain(Some(0)).collect()
    }

    /// Расширениям Проводника нужен COM в однопоточном режиме — на время вызова.
    pub struct Com(bool);

    impl Com {
        pub fn init() -> Self {
            // S_FALSE (уже инициализирован) тоже требует парного CoUninitialize.
            let hr = unsafe {
                CoInitializeEx(
                    null(),
                    (COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE) as u32,
                )
            };
            Self(hr >= 0)
        }
    }

    impl Drop for Com {
        fn drop(&mut self) {
            if self.0 {
                unsafe { CoUninitialize() };
            }
        }
    }

    pub fn open(plan: &Plan) -> Result<(), String> {
        let _com = Com::init();
        let verb = wide("open");
        let file = wide(&plan.file);
        let dir = plan.dir.as_ref().map(wide);
        let mut info = SHELLEXECUTEINFOW {
            cbSize: size_of::<SHELLEXECUTEINFOW>() as u32,
            // NOASYNC: поток сразу завершится, ждать DDE некому. NO_UI: ошибку вернём сами.
            fMask: SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
            lpVerb: verb.as_ptr(),
            lpFile: file.as_ptr(),
            lpDirectory: dir.as_ref().map_or(null(), |d| d.as_ptr()),
            nShow: SW_SHOWNORMAL,
            ..Default::default()
        };
        if unsafe { ShellExecuteExW(&mut info) } != 0 {
            Ok(())
        } else {
            Err(format!(
                "не удалось открыть {}: {}",
                plan.file,
                std::io::Error::last_os_error()
            ))
        }
    }

    pub fn reveal(path: &Path) -> Result<(), String> {
        let _com = Com::init();
        let pidl = unsafe { ILCreateFromPathW(wide(path).as_ptr()) };
        if pidl.is_null() {
            return Err(format!("не найдено: {}", path.display()));
        }
        // Без списка выделенных: открыть папку родителя и выделить сам объект.
        let hr = unsafe { SHOpenFolderAndSelectItems(pidl, 0, null_mut(), 0) };
        unsafe { ILFree(pidl) };
        if hr >= 0 {
            Ok(())
        } else {
            Err(format!(
                "не удалось показать {}: 0x{hr:08X}",
                path.display()
            ))
        }
    }
}

#[cfg(windows)]
pub use os::Com;

/// Вне Windows запускать нечего: сборка и тесты на других системах.
#[cfg(not(windows))]
mod os {
    use std::path::Path;

    use super::Plan;

    pub fn open(_: &Plan) -> Result<(), String> {
        Err("запуск работает только в Windows".into())
    }

    pub fn reveal(_: &Path) -> Result<(), String> {
        Err("запуск работает только в Windows".into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uwp_apps_open_through_apps_folder() {
        let plan = plan(
            &ItemKind::App,
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        )
        .unwrap();
        assert_eq!(
            plan.file,
            "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
        );
        assert_eq!(plan.dir, None);
    }

    #[test]
    fn links_open_as_is_and_need_a_scheme() {
        let plan = plan(&ItemKind::Url, " https://example.com ").unwrap();
        assert_eq!(plan.file, "https://example.com");
        assert!(super::plan(&ItemKind::Url, "example.com").is_err());
        assert!(super::plan(&ItemKind::Url, "  ").is_err());
    }

    #[test]
    fn files_open_in_their_folder_and_must_exist() {
        let dir = std::env::temp_dir().join(format!("ws-launch-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let file = dir.join("Отчёт.txt");
        std::fs::write(&file, "x").unwrap();
        let target = file.to_string_lossy();

        let opened = plan(&ItemKind::File, &target).unwrap();
        assert_eq!(opened.dir.as_deref(), Some(&*dir.to_string_lossy()));
        // Программа по пути — так же, как файл.
        assert_eq!(plan(&ItemKind::App, &target).unwrap(), opened);
        assert_eq!(
            plan(&ItemKind::Folder, &dir.to_string_lossy()).unwrap().dir,
            None
        );

        std::fs::remove_dir_all(&dir).unwrap();
        assert!(plan(&ItemKind::File, &target)
            .unwrap_err()
            .contains("не найдено"));
    }
}
