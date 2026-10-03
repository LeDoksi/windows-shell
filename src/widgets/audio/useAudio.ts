import { useCallback, useEffect, useState } from "react";
import { backend, type AudioFlow, type AudioState } from "../../shared/backend";
import { clampVolume } from "./settings";

const report = (e: unknown) => console.error("[audio]", e);

/**
 * Звук из Rust с подпиской на `audio_changed`: громкость клавишами клавиатуры, новое устройство
 * и всё, что поменяли в «Параметрах», приходит само. Свои изменения показываем сразу,
 * не дожидаясь события.
 */
export function useAudio() {
  const [state, setState] = useState<AudioState | null>(null);

  useEffect(() => {
    let alive = true;
    const off = backend.on("audio_changed", (next) => alive && setState(next));
    backend.call("audio_get").then((s) => alive && setState(s), report);
    return () => {
      alive = false;
      void off.then((f) => f());
    };
  }, []);

  const patch = (flow: AudioFlow, change: Partial<AudioState["output"]>) =>
    setState((s) => s && { ...s, [flow]: { ...s[flow], ...change } });

  const setVolume = useCallback((flow: AudioFlow, volume: number) => {
    const v = clampVolume(volume);
    patch(flow, { volume: v, muted: false });
    backend.call("audio_set_volume", { flow, volume: v }).catch(report);
  }, []);

  const setMute = useCallback((flow: AudioFlow, muted: boolean) => {
    patch(flow, { muted });
    backend.call("audio_set_mute", { flow, muted }).catch(report);
  }, []);

  const setDefault = useCallback((flow: AudioFlow, id: string) => {
    patch(flow, { defaultId: id });
    backend.call("audio_set_default", { id }).catch(report);
  }, []);

  const applyProfile = useCallback((deviceId: string, volume: number) => {
    patch("output", { defaultId: deviceId, volume, muted: false });
    backend.call("audio_apply_profile", { deviceId, volume }).catch(report);
  }, []);

  return { state, setVolume, setMute, setDefault, applyProfile };
}
