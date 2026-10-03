import { useCallback, useEffect, useState } from "react";
import { backend, type MediaAction, type MediaInfo } from "../../shared/backend";

/** Что играет сейчас (`media_changed` от Rust) и кнопки управления. */
export function useMedia() {
  const [media, setMedia] = useState<MediaInfo | null>(null);

  useEffect(() => {
    let alive = true;
    const off = backend.on("media_changed", (next) => alive && setMedia(next));
    backend.call("media_get").then(
      (m) => alive && setMedia(m),
      (e) => console.error("[media]", e),
    );
    return () => {
      alive = false;
      void off.then((f) => f());
    };
  }, []);

  const control = useCallback((action: MediaAction) => {
    // Пауза видна сразу; трек сменится событием.
    if (action === "playPause") setMedia((m) => m && { ...m, playing: !m.playing });
    backend.call("media_control", { action }).catch((e) => console.error("[media]", e));
  }, []);

  return { media, control };
}
