import { Icon, Tooltip } from "../../shared/ui";
import type { CompactProps } from "../types";
import { volumeIcon, wheelVolume, type AudioSettings } from "./settings";
import { useAudio } from "./useAudio";

/** Значок звука в хвосте дока: колесо — громкость, средний клик — mute, клик — flyout. */
export function AudioCompact({ settings, open, onToggle }: CompactProps<AudioSettings>) {
  const { state, setVolume, setMute } = useAudio();
  const out = state?.output;
  const label = !out?.defaultId
    ? "Нет устройства вывода"
    : out.muted
      ? "Звук выключен"
      : `Громкость ${out.volume}%`;
  return (
    <Tooltip text={label}>
      <button
        type="button"
        className="ws-tail__item"
        aria-label={label}
        aria-expanded={open}
        aria-haspopup="dialog"
        onClick={(e) => onToggle(e.currentTarget)}
        onWheel={(e) =>
          out && setVolume("output", wheelVolume(out.volume, e.deltaY, settings.step))
        }
        onMouseDown={(e) => e.button === 1 && e.preventDefault()}
        onAuxClick={(e) => e.button === 1 && out && setMute("output", !out.muted)}
      >
        <Icon name={volumeIcon(out?.volume ?? 0, out?.muted ?? !out?.defaultId)} size={20} />
      </button>
    </Tooltip>
  );
}
