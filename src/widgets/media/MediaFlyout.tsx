import { useEffect, useRef } from "react";
import type { WidgetProps } from "../types";
import { NowPlaying } from "./NowPlaying";
import { useMedia } from "./useMedia";

export function MediaFlyout({ close }: WidgetProps<object>) {
  const { media, control } = useMedia();
  // Музыка закончилась, пока flyout открыт: кнопки в панели уже нет — закрываемся.
  const had = useRef(false);
  useEffect(() => {
    if (media) had.current = true;
    else if (had.current) close?.();
  }, [media, close]);
  if (!media) return <div className="ws-flyout__hint">Сейчас ничего не играет</div>;
  return <NowPlaying media={media} onControl={control} size="large" />;
}
