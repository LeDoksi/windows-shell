import { audioWidget } from "./audio/manifest";
import { mediaWidget } from "./media/manifest";
import type { AnyWidget } from "./types";

/** Все виджеты сборки. Новый виджет = папка в `src/widgets/` и строка здесь. */
export const widgetRegistry: AnyWidget[] = [audioWidget, mediaWidget];

export function findWidget(id: string): AnyWidget | undefined {
  return widgetRegistry.find((w) => w.id === id);
}
