import type { AudioState } from "../../shared/backend";
import type { IconName } from "../../shared/ui";

/** Профиль: «Наушники 40 %» — устройство и громкость одним кликом или хоткеем. */
export type AudioProfile = {
  id: string;
  name: string;
  deviceId: string;
  volume: number;
  /** `Ctrl+Alt+1`; хоткеи регистрирует Rust (`src-tauri/src/hotkeys.rs`). */
  hotkey: string | null;
};

export type AudioSettings = {
  /** Шаг колеса мыши над значком, %. */
  step: number;
  showApps: boolean;
  showMic: boolean;
  profiles: AudioProfile[];
  /** Хоткей «следующее устройство вывода». */
  nextOutputHotkey: string | null;
};

export const audioDefaults: AudioSettings = {
  step: 2,
  showApps: true,
  showMic: true,
  profiles: [],
  nextOutputHotkey: null,
};

export const clampVolume = (v: number) => Math.min(100, Math.max(0, Math.round(v)));

/** Колесо над значком: вверх — громче. */
export function wheelVolume(volume: number, deltaY: number, step: number): number {
  return clampVolume(volume + (deltaY < 0 ? step : -step));
}

export function volumeIcon(volume: number, muted: boolean): IconName {
  if (muted || volume === 0) return "speakerMuted";
  return volume < 34 ? "speakerLow" : "speaker";
}

/** «Наушники (Sony WH-1000XM5)» → «Наушники»: так короче на кнопке профиля. */
export function shortDeviceName(name: string): string {
  const short = name.replace(/\s*\(.*\)\s*$/, "").trim();
  return short || name;
}

/** Короткое имя, а если оно у нескольких устройств («Динамики (Realtek)», «Динамики (G733)») — полное. */
export function profileDeviceName(name: string, all: string[]): string {
  const short = shortDeviceName(name);
  return all.filter((n) => shortDeviceName(n) === short).length > 1 ? name : short;
}

/** Новый профиль из того, что сейчас: устройство вывода по умолчанию и его громкость. */
export function profileFromState(state: AudioState, existing: AudioProfile[]): AudioProfile | null {
  const { defaultId, devices, volume } = state.output;
  const device = devices.find((d) => d.id === defaultId);
  if (!device) return null;
  let n = existing.length + 1;
  while (existing.some((p) => p.id === `profile-${n}`)) n += 1;
  return {
    id: `profile-${n}`,
    name: `${profileDeviceName(
      device.name,
      devices.map((d) => d.name),
    )} ${volume}%`,
    deviceId: device.id,
    volume,
    hotkey: null,
  };
}

/** Профиль, который сейчас в силе: то же устройство и та же громкость. */
export function activeProfile(profiles: AudioProfile[], state: AudioState): string | null {
  const { defaultId, volume } = state.output;
  return profiles.find((p) => p.deviceId === defaultId && p.volume === volume)?.id ?? null;
}
