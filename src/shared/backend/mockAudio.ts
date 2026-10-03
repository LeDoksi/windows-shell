import type { AudioMixer, AudioState, Commands, Events, MediaInfo } from "./types";

type Emit = <E extends keyof Events>(event: E, payload: Events[E]) => void;

type AudioCommands =
  | "audio_get"
  | "audio_set_volume"
  | "audio_set_mute"
  | "audio_set_default"
  | "audio_apply_profile"
  | "audio_mixer"
  | "audio_session_set"
  | "media_get"
  | "media_control";

type Handlers = { [C in AudioCommands]: (args: Commands[C]["args"]) => Commands[C]["result"] };

/** Звук и «сейчас играет» для браузерного режима: меняется по командам, как в Windows. */
export function createMockAudio(emit: Emit): Handlers {
  let audio: AudioState = {
    output: {
      devices: [
        { id: "sony", name: "Наушники (Sony WH-1000XM5)" },
        { id: "realtek", name: "Динамики (Realtek)" },
        { id: "dell", name: "Монитор DELL (HDMI)" },
      ],
      defaultId: "sony",
      volume: 64,
      muted: false,
    },
    input: {
      devices: [
        { id: "yeti", name: "Микрофон (Blue Yeti)" },
        { id: "webcam", name: "Микрофон веб-камеры" },
      ],
      defaultId: "yeti",
      volume: 80,
      muted: false,
    },
  };
  let sessions = [
    { id: "chrome", name: "Google Chrome", exe: null, volume: 80, muted: false },
    { id: "discord", name: "Discord", exe: null, volume: 45, muted: false },
    { id: "steam", name: "Steam", exe: null, volume: 95, muted: true },
    { id: "system", name: "Системные звуки", exe: null, volume: 100, muted: false },
  ];
  let media: MediaInfo | null = {
    appId: "Spotify.exe",
    title: "Weightless",
    artist: "Marconi Union",
    playing: true,
    canPlayPause: true,
    canNext: true,
    canPrevious: true,
    cover: null,
  };
  const tracks = [
    ["Weightless", "Marconi Union"],
    ["Clair de Lune", "Claude Debussy"],
    ["Teardrop", "Massive Attack"],
  ];

  const set = (next: AudioState) => {
    audio = next;
    emit("audio_changed", structuredClone(audio));
  };
  const flow = (f: "output" | "input", change: Partial<AudioState["output"]>) =>
    set({ ...audio, [f]: { ...audio[f], ...change } });
  // Уровни «играют», пока звук не выключен.
  const level = (on: boolean) => (on ? 0.25 + Math.random() * 0.6 : 0);

  return {
    audio_get: () => structuredClone(audio),
    audio_set_volume: ({ flow: f, volume }) => flow(f, { volume, muted: false }),
    audio_set_mute: ({ flow: f, muted }) => flow(f, { muted }),
    audio_set_default: ({ id }) => {
      const f = audio.output.devices.some((d) => d.id === id) ? "output" : "input";
      flow(f, { defaultId: id });
    },
    audio_apply_profile: ({ deviceId, volume }) =>
      flow("output", { defaultId: deviceId, volume, muted: false }),
    audio_mixer: (): AudioMixer => ({
      outputPeak: level(!audio.output.muted && media?.playing === true),
      inputPeak: level(!audio.input.muted) * 0.4,
      sessions: sessions.map((s) => ({
        ...s,
        peak: level(!s.muted && s.id === "chrome" && media?.playing === true),
      })),
    }),
    audio_session_set: ({ id, volume, muted }) => {
      sessions = sessions.map((s) =>
        s.id === id ? { ...s, volume: volume ?? s.volume, muted: muted ?? s.muted } : s,
      );
    },
    media_get: () => structuredClone(media),
    media_control: ({ action }) => {
      if (!media) return;
      if (action === "playPause") {
        media = { ...media, playing: !media.playing };
      } else {
        const i = tracks.findIndex(([title]) => title === media?.title);
        const next = tracks[(i + (action === "next" ? 1 : tracks.length - 1)) % tracks.length];
        media = { ...media, title: next[0], artist: next[1], playing: true };
      }
      emit("media_changed", structuredClone(media));
    },
  };
}
