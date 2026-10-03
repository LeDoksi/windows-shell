import type { MediaAction, MediaInfo } from "../../shared/backend";
import { Icon, IconButton } from "../../shared/ui";
import { cx } from "../../shared/ui/cx";

type Props = {
  media: MediaInfo;
  onControl: (action: MediaAction) => void;
  size: "small" | "large";
};

/** Обложка, название, исполнитель и кнопки «назад», «пауза», «вперёд». */
export function NowPlaying({ media, onControl, size }: Props) {
  return (
    <div className={cx("ws-now-playing", `ws-now-playing--${size}`)}>
      {media.cover ? (
        <img className="ws-now-playing__cover" src={media.cover} alt="" />
      ) : (
        <span className="ws-now-playing__cover">
          <Icon name="music" size={size === "large" ? 28 : 18} />
        </span>
      )}
      <div className="ws-now-playing__text">
        <div className="ws-now-playing__title" title={media.title}>
          {media.title || "Без названия"}
        </div>
        {media.artist && (
          <div className="ws-flyout__hint ws-now-playing__artist" title={media.artist}>
            {media.artist}
          </div>
        )}
      </div>
      <div className="ws-now-playing__controls">
        <IconButton
          icon="previous"
          label="Предыдущий"
          disabled={!media.canPrevious}
          onClick={() => onControl("previous")}
        />
        <IconButton
          icon={media.playing ? "pause" : "play"}
          label={media.playing ? "Пауза" : "Играть"}
          disabled={!media.canPlayPause}
          onClick={() => onControl("playPause")}
        />
        <IconButton
          icon="next"
          label="Следующий"
          disabled={!media.canNext}
          onClick={() => onControl("next")}
        />
      </div>
    </div>
  );
}
