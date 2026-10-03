import { defineWidget } from "../types";
import { MediaCompact } from "./MediaCompact";
import { MediaFlyout } from "./MediaFlyout";

/** «Сейчас играет»: музыка и видео, которые Windows показывает в своей плашке громкости. */
export const mediaWidget = defineWidget({
  id: "media",
  name: "Сейчас играет",
  description: "Трек, пауза и переключение в верхней панели, пока что-то играет.",
  icon: "music",
  slot: "launcher",
  defaults: {},
  Compact: MediaCompact,
  Flyout: MediaFlyout,
});
