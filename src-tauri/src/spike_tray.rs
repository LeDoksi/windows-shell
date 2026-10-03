//! Спайк WSH-44: свой трей. Включается `WS_TRAY=1` поверх спайка дока (`--features spike`).
//!
//! Подход ManagedShell/Cairo: своё скрытое topmost-окно класса `Shell_TrayWnd`. `Shell_NotifyIcon`
//! находит его первым (`FindWindow` идёт по z-order) и шлёт `WM_COPYDATA` с `SHELLTRAYDATA`.
//! Мы разбираем иконку и сразу пересылаем то же сообщение штатному трею Explorer — он продолжает
//! работать как раньше. Клики отдаём программам через их `uCallbackMessage`.
//! Сторож, возврат и `TaskbarCreated` при выходе — в `spike_taskbar` (`guard(tray = true)`).

use std::collections::BTreeMap;
use std::sync::atomic::{AtomicIsize, Ordering};
use std::sync::Mutex;

use windows_sys::Win32::Foundation::{CloseHandle, HWND, LPARAM, LRESULT, POINT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::DeleteObject;
use windows_sys::Win32::System::DataExchange::COPYDATASTRUCT;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows_sys::Win32::UI::HiDpi::{
    SetThreadDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AllowSetForegroundWindow, CreateWindowExW, DefWindowProcW, DispatchMessageW, FindWindowExW,
    GetCursorPos, GetIconInfo, GetMessageW, GetWindowThreadProcessId, PostMessageW,
    PostQuitMessage, RegisterClassExW, RegisterWindowMessageW, SendMessageTimeoutW, SendMessageW,
    SendNotifyMessageW, SetTimer, SetWindowPos, HWND_BROADCAST, HWND_TOPMOST, ICONINFO, MSG,
    SMTO_ABORTIFHUNG, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, WM_APP, WM_CLOSE, WM_CONTEXTMENU,
    WM_COPYDATA, WM_DESTROY, WM_LBUTTONDBLCLK, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_RBUTTONDOWN,
    WM_RBUTTONUP, WM_TIMER, WM_USER, WNDCLASSEXW, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_POPUP,
};

/// Наше окно `Shell_TrayWnd` (0 — не запущено).
static OUR: AtomicIsize = AtomicIsize::new(0);
static ICONS: Mutex<BTreeMap<(u32, u32), TrayIcon>> = Mutex::new(BTreeMap::new());

/// Тестовые команды окну трея: wParam — номер иконки, lParam — вид клика (1 левый, 2 правый, 3 двойной).
pub const WM_SPIKE_CLICK: u32 = WM_APP + 1;
/// Записать список иконок в лог.
pub const WM_SPIKE_DUMP: u32 = WM_APP + 2;

const NIM_ADD: u32 = 0;
const NIM_MODIFY: u32 = 1;
const NIM_DELETE: u32 = 2;
const NIM_SETVERSION: u32 = 4;
const NIF_MESSAGE: u32 = 0x1;
const NIF_ICON: u32 = 0x2;
const NIF_TIP: u32 = 0x4;
const NIF_STATE: u32 = 0x8;
const NIS_HIDDEN: u32 = 0x1;
const NIN_SELECT: u32 = WM_USER;
const SHELLTRAY_SIGNATURE: u32 = 0x3475_3423;

#[derive(Debug, Default, Clone, PartialEq)]
pub struct TrayIcon {
    pub hwnd: u32,
    pub uid: u32,
    pub callback: u32,
    pub version: u32,
    pub tip: String,
    pub hicon: u32,
    pub hidden: bool,
}

/// Одно сообщение `Shell_NotifyIcon`: `NIM_*` и `NOTIFYICONDATA` (32-битные хэндлы, как в Explorer).
#[derive(Debug, PartialEq)]
pub struct Notify {
    pub message: u32,
    pub flags: u32,
    pub icon: TrayIcon,
    pub state: u32,
    pub state_mask: u32,
}

fn u32_at(b: &[u8], off: usize) -> Option<u32> {
    Some(u32::from_le_bytes(b.get(off..off + 4)?.try_into().ok()?))
}

fn utf16_at(b: &[u8], off: usize, chars: usize) -> String {
    let raw = b.get(off..(off + chars * 2).min(b.len())).unwrap_or(&[]);
    let units: Vec<u16> = raw
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .take_while(|&u| u != 0)
        .collect();
    String::from_utf16_lossy(&units)
}

/// Разбор `SHELLTRAYDATA` из `WM_COPYDATA` (dwData = 1).
/// Смещения `NOTIFYICONDATAW` в 32-битном виде: hWnd 4, uID 8, uFlags 12, uCallbackMessage 16,
/// hIcon 20, szTip 24 (128 символов), dwState 280, dwStateMask 284, uVersion 800.
pub fn parse(data: &[u8]) -> Option<Notify> {
    if u32_at(data, 0)? != SHELLTRAY_SIGNATURE {
        return None;
    }
    let message = u32_at(data, 4)?;
    let n = data.get(8..)?;
    let flags = u32_at(n, 12)?;
    Some(Notify {
        message,
        flags,
        icon: TrayIcon {
            hwnd: u32_at(n, 4)?,
            uid: u32_at(n, 8)?,
            callback: u32_at(n, 16).unwrap_or(0),
            version: u32_at(n, 800).unwrap_or(0),
            tip: utf16_at(n, 24, 128),
            hicon: u32_at(n, 20).unwrap_or(0),
            hidden: false,
        },
        state: u32_at(n, 280).unwrap_or(0),
        state_mask: u32_at(n, 284).unwrap_or(0),
    })
}

/// Применить сообщение к списку иконок.
pub fn apply(icons: &mut BTreeMap<(u32, u32), TrayIcon>, m: &Notify) {
    let key = (m.icon.hwnd, m.icon.uid);
    match m.message {
        NIM_ADD | NIM_MODIFY => {
            let e = icons.entry(key).or_insert_with(|| TrayIcon {
                hwnd: m.icon.hwnd,
                uid: m.icon.uid,
                ..TrayIcon::default()
            });
            if m.flags & NIF_MESSAGE != 0 {
                e.callback = m.icon.callback;
            }
            if m.flags & NIF_ICON != 0 {
                e.hicon = m.icon.hicon;
            }
            if m.flags & NIF_TIP != 0 {
                e.tip = m.icon.tip.clone();
            }
            if m.flags & NIF_STATE != 0 && m.state_mask & NIS_HIDDEN != 0 {
                e.hidden = m.state & NIS_HIDDEN != 0;
            }
        }
        NIM_DELETE => {
            icons.remove(&key);
        }
        NIM_SETVERSION => {
            if let Some(e) = icons.get_mut(&key) {
                e.version = m.icon.version;
            }
        }
        _ => {}
    }
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain([0]).collect()
}

fn log(msg: &str) {
    crate::spike_taskbar::log(&format!("tray: {msg}"));
}

/// Штатное окно трея Explorer — любое `Shell_TrayWnd`, кроме нашего.
fn explorer_tray() -> HWND {
    let class = wide("Shell_TrayWnd");
    let ours = OUR.load(Ordering::Relaxed) as HWND;
    let mut h: HWND = std::ptr::null_mut();
    loop {
        h = unsafe { FindWindowExW(std::ptr::null_mut(), h, class.as_ptr(), std::ptr::null()) };
        if h.is_null() || h != ours {
            return h;
        }
    }
}

fn owner(hwnd: u32) -> (u32, String) {
    let mut pid = 0;
    unsafe { GetWindowThreadProcessId(hwnd as usize as HWND, &mut pid) };
    let mut buf = [0u16; 260];
    let mut len = buf.len() as u32;
    let exe = unsafe {
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if h.is_null() {
            String::new()
        } else {
            let ok = QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut len) != 0;
            CloseHandle(h);
            if ok {
                String::from_utf16_lossy(&buf[..len as usize])
            } else {
                String::new()
            }
        }
    };
    let name = exe.rsplit('\\').next().unwrap_or("").to_string();
    (pid, name)
}

/// Хэндл иконки из чужого процесса годится и у нас (USER-объекты общие на сессию)?
fn icon_ok(hicon: u32) -> bool {
    if hicon == 0 {
        return false;
    }
    let mut info: ICONINFO = unsafe { std::mem::zeroed() };
    let ok = unsafe { GetIconInfo(hicon as usize as _, &mut info) } != 0;
    if ok {
        unsafe {
            DeleteObject(info.hbmColor);
            DeleteObject(info.hbmMask);
        }
    }
    ok
}

fn sorted() -> Vec<TrayIcon> {
    ICONS.lock().unwrap().values().cloned().collect()
}

fn dump() {
    let icons = sorted();
    log(&format!("{} icons", icons.len()));
    for (i, ic) in icons.iter().enumerate() {
        let (pid, exe) = owner(ic.hwnd);
        log(&format!(
            "#{i} {exe} pid {pid} v{} uid {} cb {:#x} icon {} hidden {} tip {:?}",
            ic.version,
            ic.uid,
            ic.callback,
            if icon_ok(ic.hicon) { "ok" } else { "none" },
            ic.hidden,
            ic.tip
        ));
    }
}

fn makelong(lo: u32, hi: u32) -> usize {
    ((lo & 0xffff) | (hi & 0xffff) << 16) as usize
}

/// Переслать клик программе-владельцу иконки так, как это делает Explorer.
fn click(index: usize, kind: isize) {
    let Some(ic) = sorted().into_iter().nth(index) else {
        log(&format!("click: no icon #{index}"));
        return;
    };
    let (pid, exe) = owner(ic.hwnd);
    // Программе понадобится вывести окно или меню на передний план.
    unsafe { AllowSetForegroundWindow(pid) };
    let mut p = POINT { x: 0, y: 0 };
    unsafe { GetCursorPos(&mut p) };
    let msgs: &[u32] = match (kind, ic.version >= 4) {
        (1, true) => &[WM_LBUTTONDOWN, WM_LBUTTONUP, NIN_SELECT],
        (2, true) => &[WM_RBUTTONDOWN, WM_RBUTTONUP, WM_CONTEXTMENU],
        (3, true) => &[WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK, WM_LBUTTONUP],
        (1, false) => &[WM_LBUTTONDOWN, WM_LBUTTONUP],
        (2, false) => &[WM_RBUTTONDOWN, WM_RBUTTONUP],
        _ => &[WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK, WM_LBUTTONUP],
    };
    for &m in msgs {
        // Версия 4: wParam — точка привязки (x, y), lParam — (событие, uID).
        // Старые версии: wParam — uID, lParam — событие.
        let (w, l) = if ic.version >= 4 {
            (
                makelong(p.x as u32, p.y as u32),
                makelong(m, ic.uid) as isize,
            )
        } else {
            (ic.uid as usize, m as isize)
        };
        unsafe { PostMessageW(ic.hwnd as usize as HWND, ic.callback, w, l) };
    }
    log(&format!(
        "click kind {kind} -> #{index} {exe} v{} ({} messages)",
        ic.version,
        msgs.len()
    ));
}

fn raise() {
    let h = OUR.load(Ordering::Relaxed) as HWND;
    unsafe {
        SetWindowPos(
            h,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
        )
    };
}

unsafe extern "system" fn tray_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_COPYDATA => {
            let cds = &*(lparam as *const COPYDATASTRUCT);
            if cds.dwData == 1 && !cds.lpData.is_null() {
                let data = std::slice::from_raw_parts(cds.lpData as *const u8, cds.cbData as usize);
                if let Some(m) = parse(data) {
                    apply(&mut ICONS.lock().unwrap(), &m);
                }
            }
            // Всё — и иконки, и прочие WM_COPYDATA (AppBar и т. п.) — отдаём штатному трею.
            let explorer = explorer_tray();
            if explorer.is_null() {
                return 1;
            }
            let mut result = 0usize;
            SendMessageTimeoutW(
                explorer,
                WM_COPYDATA,
                wparam,
                lparam,
                SMTO_ABORTIFHUNG,
                2000,
                &mut result,
            );
            result as LRESULT
        }
        WM_TIMER => {
            // Explorer поднимает свою панель — держим наше окно выше, иначе иконки уйдут ему.
            raise();
            0
        }
        WM_SPIKE_CLICK => {
            click(wparam, lparam);
            0
        }
        WM_SPIKE_DUMP => {
            dump();
            0
        }
        WM_DESTROY => {
            OUR.store(0, Ordering::Relaxed);
            PostQuitMessage(0);
            0
        }
        _ => {
            if (WM_USER..WM_APP).contains(&msg) {
                // Сообщения, которые другие программы шлют «панели задач», а не трею.
                log(&format!("unhandled message {msg:#x} wparam {wparam:#x}"));
            }
            DefWindowProcW(hwnd, msg, wparam, lparam)
        }
    }
}

/// Поднять своё окно трея в отдельном потоке со своим циклом сообщений.
pub fn start() {
    std::thread::spawn(|| unsafe {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        let class = wide("Shell_TrayWnd");
        let hinst = GetModuleHandleW(std::ptr::null());
        let wc = WNDCLASSEXW {
            cbSize: std::mem::size_of::<WNDCLASSEXW>() as u32,
            lpfnWndProc: Some(tray_proc),
            hInstance: hinst,
            lpszClassName: class.as_ptr(),
            ..std::mem::zeroed()
        };
        RegisterClassExW(&wc);
        let hwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            class.as_ptr(),
            std::ptr::null(),
            WS_POPUP,
            0,
            0,
            0,
            0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            hinst,
            std::ptr::null(),
        );
        if hwnd.is_null() {
            log("CreateWindowEx failed");
            return;
        }
        OUR.store(hwnd as isize, Ordering::Relaxed);
        raise();
        SetTimer(hwnd, 1, 1000, None);
        log(&format!(
            "our Shell_TrayWnd {hwnd:?}, explorer's {:?}",
            explorer_tray()
        ));
        // Программы заново добавляют свои иконки — теперь уже через нас.
        SendNotifyMessageW(
            HWND_BROADCAST,
            RegisterWindowMessageW(wide("TaskbarCreated").as_ptr()),
            0,
            0,
        );
        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) > 0 {
            DispatchMessageW(&msg);
        }
        log("tray thread stopped");
    });
}

/// Закрыть наше окно трея (синхронно). Безопасно звать, если оно не запущено.
pub fn stop() {
    let h = OUR.load(Ordering::Relaxed) as HWND;
    if !h.is_null() {
        unsafe { SendMessageW(h, WM_CLOSE, 0, 0) };
        log("our Shell_TrayWnd closed");
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn notify(message: u32, flags: u32, hwnd: u32, uid: u32, tip: &str) -> Vec<u8> {
        let mut b = vec![0u8; 8 + 956];
        b[0..4].copy_from_slice(&SHELLTRAY_SIGNATURE.to_le_bytes());
        b[4..8].copy_from_slice(&message.to_le_bytes());
        let n = 8;
        let put = |b: &mut Vec<u8>, off: usize, v: u32| {
            b[n + off..n + off + 4].copy_from_slice(&v.to_le_bytes())
        };
        put(&mut b, 0, 956);
        put(&mut b, 4, hwnd);
        put(&mut b, 8, uid);
        put(&mut b, 12, flags);
        put(&mut b, 16, 0x8001);
        put(&mut b, 20, 0x1234);
        for (i, u) in tip.encode_utf16().enumerate() {
            b[n + 24 + i * 2..n + 26 + i * 2].copy_from_slice(&u.to_le_bytes());
        }
        put(&mut b, 800, 4);
        b
    }

    #[test]
    fn parses_shelltraydata() {
        let m = parse(&notify(
            NIM_ADD,
            NIF_MESSAGE | NIF_ICON | NIF_TIP,
            0x10,
            7,
            "Steam",
        ))
        .unwrap();
        assert_eq!(m.message, NIM_ADD);
        assert_eq!(
            (m.icon.hwnd, m.icon.uid, m.icon.callback),
            (0x10, 7, 0x8001)
        );
        assert_eq!(m.icon.tip, "Steam");
        assert_eq!(m.icon.hicon, 0x1234);
        assert!(parse(&[0u8; 16]).is_none(), "чужая подпись");
        assert!(
            parse(&[0x23, 0x34, 0x75, 0x34]).is_none(),
            "обрезанные данные"
        );
    }

    #[test]
    fn add_modify_version_delete() {
        let mut icons = BTreeMap::new();
        apply(
            &mut icons,
            &parse(&notify(NIM_ADD, NIF_MESSAGE | NIF_TIP, 1, 1, "a")).unwrap(),
        );
        apply(
            &mut icons,
            &parse(&notify(NIM_MODIFY, NIF_TIP, 1, 1, "b")).unwrap(),
        );
        apply(
            &mut icons,
            &parse(&notify(NIM_SETVERSION, 0, 1, 1, "")).unwrap(),
        );
        let ic = &icons[&(1, 1)];
        assert_eq!(
            (ic.tip.as_str(), ic.callback, ic.version, ic.hicon),
            ("b", 0x8001, 4, 0)
        );
        apply(
            &mut icons,
            &parse(&notify(NIM_DELETE, 0, 1, 1, "")).unwrap(),
        );
        assert!(icons.is_empty());
    }

    #[test]
    fn click_params_pack_coordinates() {
        assert_eq!(makelong(0x0100, 0x0200), 0x0200_0100);
        // Отрицательные координаты (монитор слева) упаковываются как 16-битные.
        assert_eq!(makelong(-5_i32 as u32, 3), 0x0003_fffb);
    }
}
