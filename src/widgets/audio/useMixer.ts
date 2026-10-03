import { useCallback, useEffect, useRef, useState } from "react";
import { backend, type AudioMixer } from "../../shared/backend";

/** Уровни скачут быстро: 10 раз в секунду хватает глазу и не грузит процессор. */
const POLL_MS = 100;
/** Столько после своего изменения не верим опросу: ответ мог уйти до него. */
const HOLD_MS = 500;

/** Микшер (приложения и уровни), пока компонент на экране: событий об уровнях у Windows нет. */
export function useMixer() {
  const [mixer, setMixer] = useState<AudioMixer | null>(null);
  const held = useRef(new Map<string, number>());

  useEffect(() => {
    let alive = true;
    let timer = 0;
    const poll = async () => {
      try {
        const next = await backend.call("audio_mixer");
        if (!alive) return;
        const now = Date.now();
        setMixer((prev) => ({
          ...next,
          sessions: next.sessions.map((s) => {
            const until = held.current.get(s.id) ?? 0;
            const mine = prev?.sessions.find((p) => p.id === s.id);
            return until > now && mine ? { ...s, volume: mine.volume, muted: mine.muted } : s;
          }),
        }));
      } catch (e) {
        console.error("[audio] mixer", e);
      }
      if (alive) timer = window.setTimeout(poll, POLL_MS);
    };
    void poll();
    return () => {
      alive = false;
      window.clearTimeout(timer);
    };
  }, []);

  const setSession = useCallback((id: string, change: { volume?: number; muted?: boolean }) => {
    held.current.set(id, Date.now() + HOLD_MS);
    setMixer(
      (m) =>
        m && { ...m, sessions: m.sessions.map((s) => (s.id === id ? { ...s, ...change } : s)) },
    );
    backend.call("audio_session_set", { id, ...change }).catch((e) => console.error("[audio]", e));
  }, []);

  return { mixer, setSession };
}
