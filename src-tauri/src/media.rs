//! «Сейчас играет» (этап 5: WSH-33). Музыка и видео, которые Windows показывает в своей
//! плашке громкости: `GlobalSystemMediaTransportControlsSessionManager` (WinRT).
//! Сервис слушает смену сессии, трека и паузы и рассылает `media_changed`.

use std::sync::Mutex;

use serde::{Deserialize, Serialize};

use crate::widgets::WidgetService;

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Media {
    /// AUMID приложения-источника: Spotify, браузер…
    pub app_id: String,
    pub title: String,
    pub artist: String,
    pub playing: bool,
    pub can_play_pause: bool,
    pub can_next: bool,
    pub can_previous: bool,
    /// Обложка как `data:`-URL.
    pub cover: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum MediaAction {
    PlayPause,
    Next,
    Previous,
}

/// Последнее разосланное: окно, открывшееся позже, получает его сразу.
static LAST: Mutex<Option<Media>> = Mutex::new(None);

#[tauri::command]
pub fn media_get() -> Option<Media> {
    LAST.lock().expect("media").clone()
}

#[tauri::command]
pub async fn media_control(action: MediaAction) -> Result<(), String> {
    tauri::async_runtime::spawn_blocking(move || os::control(action))
        .await
        .map_err(|e| e.to_string())?
}

#[derive(Default)]
pub struct Service {
    #[cfg(windows)]
    running: Option<os::Watcher>,
}

impl WidgetService for Service {
    fn id(&self) -> &'static str {
        "media"
    }

    fn start(&mut self, app: &tauri::AppHandle) {
        #[cfg(windows)]
        if self.running.is_none() {
            self.running = Some(os::Watcher::start(app.clone()));
        }
        #[cfg(not(windows))]
        let _ = app;
    }

    fn stop(&mut self) {
        #[cfg(windows)]
        if let Some(w) = self.running.take() {
            w.stop();
        }
        *LAST.lock().expect("media") = None;
    }
}

/// Обложки больше этого не читаем: в событие идёт всё целиком.
pub const MAX_COVER: u64 = 2 * 1024 * 1024;

#[cfg(windows)]
mod os {
    use std::sync::mpsc::{self, RecvTimeoutError, Sender};
    use std::time::Duration;

    use base64::Engine;
    use tauri::{AppHandle, Emitter};
    use windows::Foundation::TypedEventHandler;
    use windows::Media::Control::{
        GlobalSystemMediaTransportControlsSession as Session,
        GlobalSystemMediaTransportControlsSessionManager as Manager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus as Status,
    };
    use windows::Storage::Streams::{DataReader, IRandomAccessStreamReference};

    use super::{Media, MediaAction, LAST, MAX_COVER};
    use crate::audio::Mta;

    fn err(e: windows::core::Error) -> String {
        e.message()
    }

    fn manager() -> Result<Manager, String> {
        Manager::RequestAsync()
            .and_then(|op| op.join())
            .map_err(err)
    }

    pub fn control(action: MediaAction) -> Result<(), String> {
        let _com = Mta::init();
        let Ok(session) = manager()?.GetCurrentSession() else {
            return Ok(());
        };
        let op = match action {
            MediaAction::PlayPause => session.TryTogglePlayPauseAsync(),
            MediaAction::Next => session.TrySkipNextAsync(),
            MediaAction::Previous => session.TrySkipPreviousAsync(),
        };
        op.and_then(|op| op.join()).map(|_| ()).map_err(err)
    }

    fn cover(thumb: &IRandomAccessStreamReference) -> windows::core::Result<Option<String>> {
        let stream = thumb.OpenReadAsync()?.join()?;
        let size = stream.Size()?;
        if size == 0 || size > MAX_COVER {
            return Ok(None);
        }
        let reader = DataReader::CreateDataReader(&stream)?;
        reader.LoadAsync(size as u32)?.join()?;
        let mut bytes = vec![0u8; size as usize];
        reader.ReadBytes(&mut bytes)?;
        let kind = stream
            .ContentType()
            .map(|t| t.to_string())
            .ok()
            .filter(|t| t.starts_with("image/"))
            .unwrap_or_else(|| "image/png".into());
        let data = base64::engine::general_purpose::STANDARD.encode(bytes);
        Ok(Some(format!("data:{kind};base64,{data}")))
    }

    /// Что играет сейчас. Обложку перечитываем только при смене трека.
    fn read(session: &Session, previous: Option<&Media>) -> windows::core::Result<Media> {
        let props = session.TryGetMediaPropertiesAsync()?.join()?;
        let info = session.GetPlaybackInfo()?;
        let controls = info.Controls()?;
        let title = props.Title()?.to_string();
        let artist = props.Artist()?.to_string();
        let app_id = session.SourceAppUserModelId()?.to_string();
        let same =
            previous.filter(|p| p.title == title && p.artist == artist && p.app_id == app_id);
        let cover = match same {
            Some(p) => p.cover.clone(),
            None => props
                .Thumbnail()
                .ok()
                .and_then(|t| cover(&t).ok().flatten()),
        };
        Ok(Media {
            app_id,
            title,
            artist,
            playing: info.PlaybackStatus()? == Status::Playing,
            can_play_pause: controls.IsPlayPauseToggleEnabled()?,
            can_next: controls.IsNextEnabled()?,
            can_previous: controls.IsPreviousEnabled()?,
            cover,
        })
    }

    enum Msg {
        Changed,
        Stop,
    }

    /// Обложка и название приходят отдельными событиями — ждём, пока утихнет.
    const DEBOUNCE: Duration = Duration::from_millis(150);

    pub struct Watcher(Sender<Msg>);

    impl Watcher {
        pub fn start(app: AppHandle) -> Self {
            let (tx, rx) = mpsc::channel();
            let events = tx.clone();
            std::thread::spawn(move || {
                if let Err(e) = watch(&app, events, &rx) {
                    eprintln!("[media] {e}");
                }
            });
            Self(tx)
        }

        pub fn stop(self) {
            let _ = self.0.send(Msg::Stop);
        }
    }

    fn publish(app: &AppHandle, media: Option<Media>) {
        let mut last = LAST.lock().expect("media");
        if *last != media {
            *last = media.clone();
            drop(last);
            let _ = app.emit("media_changed", media);
        }
    }

    /// Обработчик события WinRT, который только будит поток сервиса.
    fn wake<S, A>(tx: &Sender<Msg>) -> TypedEventHandler<S, A>
    where
        S: windows::core::RuntimeType + 'static,
        A: windows::core::RuntimeType + 'static,
    {
        let tx = tx.clone();
        TypedEventHandler::new(move |_, _| {
            let _ = tx.send(Msg::Changed);
            Ok(())
        })
    }

    fn watch(app: &AppHandle, tx: Sender<Msg>, rx: &mpsc::Receiver<Msg>) -> Result<(), String> {
        let _com = Mta::init();
        let manager = manager()?;
        let session_token = manager.CurrentSessionChanged(&wake(&tx)).map_err(err)?;
        // Подписка на текущую сессию: сменилась — отписываемся от старой.
        let mut subscribed: Option<(Session, i64, i64)> = None;
        'outer: loop {
            let session = manager.GetCurrentSession().ok();
            let same = match (&session, &subscribed) {
                (Some(s), Some((old, _, _))) => s == old,
                (None, None) => true,
                _ => false,
            };
            if !same {
                if let Some((old, a, b)) = subscribed.take() {
                    let _ = old.RemoveMediaPropertiesChanged(a);
                    let _ = old.RemovePlaybackInfoChanged(b);
                }
                if let Some(s) = &session {
                    let a = s.MediaPropertiesChanged(&wake(&tx));
                    let b = s.PlaybackInfoChanged(&wake(&tx));
                    if let (Ok(a), Ok(b)) = (a, b) {
                        subscribed = Some((s.clone(), a, b));
                    }
                }
            }
            let previous = LAST.lock().expect("media").clone();
            let media = session.and_then(|s| read(&s, previous.as_ref()).ok());
            publish(app, media);
            match rx.recv() {
                Ok(Msg::Changed) => {}
                Ok(Msg::Stop) | Err(_) => break,
            }
            loop {
                match rx.recv_timeout(DEBOUNCE) {
                    Ok(Msg::Changed) => {}
                    Ok(Msg::Stop) | Err(RecvTimeoutError::Disconnected) => break 'outer,
                    Err(RecvTimeoutError::Timeout) => break,
                }
            }
        }
        if let Some((old, a, b)) = subscribed {
            let _ = old.RemoveMediaPropertiesChanged(a);
            let _ = old.RemovePlaybackInfoChanged(b);
        }
        let _ = manager.RemoveCurrentSessionChanged(session_token);
        Ok(())
    }
}

#[cfg(not(windows))]
mod os {
    use super::MediaAction;

    pub fn control(_: MediaAction) -> Result<(), String> {
        Err("медиа есть только в Windows".into())
    }
}
