import { Icon, IconButton, Tooltip } from "../../shared/ui";
import type { CompactProps } from "../types";
import { useMedia } from "./useMedia";

/** В верхней панели: обложка и название (клик — flyout) и кнопка паузы. Ничего не играет — пусто. */
export function MediaCompact({ open, onToggle }: CompactProps<object>) {
  const { media, control } = useMedia();
  if (!media) return null;
  const label = media.artist ? `${media.title} — ${media.artist}` : media.title;
  return (
    <span className="ws-media-compact">
      <Tooltip text={label} placement="bottom">
        <button
          type="button"
          className="ws-media-compact__track"
          aria-label={`Сейчас играет: ${label}`}
          aria-expanded={open}
          aria-haspopup="dialog"
          onClick={(e) => onToggle(e.currentTarget.closest(".ws-media-compact") ?? e.currentTarget)}
        >
          {media.cover ? (
            <img className="ws-media-compact__cover" src={media.cover} alt="" />
          ) : (
            <Icon name="music" size={16} />
          )}
          <span className="ws-media-compact__title">{media.title || "Без названия"}</span>
        </button>
      </Tooltip>
      <IconButton
        icon={media.playing ? "pause" : "play"}
        label={media.playing ? "Пауза" : "Играть"}
        disabled={!media.canPlayPause}
        onClick={() => control("playPause")}
      />
    </span>
  );
}
