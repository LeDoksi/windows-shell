import { defineWidget } from "../types";
import { AudioCompact } from "./AudioCompact";
import { AudioFlyout } from "./AudioFlyout";
import { AudioSettings } from "./AudioSettings";
import { audioDefaults } from "./settings";

/** Свой звук вместо значка Windows: в хвосте дока (docs/WIDGETS.md, docs/SYSTEM-TAIL.md). */
export const audioWidget = defineWidget({
  id: "audio",
  name: "Звук",
  description: "Громкость, устройства, приложения и микрофон. Значок — в доке справа.",
  icon: "speaker",
  slot: "dock-tail",
  defaults: audioDefaults,
  Compact: AudioCompact,
  Flyout: AudioFlyout,
  Settings: AudioSettings,
});
