import { NowPlaying } from "./NowPlaying";
import { useMedia } from "./useMedia";

export function MediaFlyout() {
  const { media, control } = useMedia();
  if (!media) return <div className="ws-flyout__hint">Сейчас ничего не играет</div>;
  return <NowPlaying media={media} onControl={control} size="large" />;
}
