/** Прямоугольник места в ряду или сетке (CSS-пиксели окна). */
export type Slot = { left: number; top: number; right: number; bottom: number };

export function slotOf(el: Element): Slot {
  const r = el.getBoundingClientRect();
  return { left: r.left, top: r.top, right: r.right, bottom: r.bottom };
}

/**
 * На какое место встанет перетаскиваемое: ближайшее к курсору место сетки.
 * Места снимаются в начале перетаскивания и не меняются, пока плитки переставляются:
 * так индекс не скачет туда-сюда. Пустой список — 0.
 */
export function nearestSlot(slots: Slot[], x: number, y: number): number {
  let best = 0;
  let bestDistance = Infinity;
  slots.forEach((s, i) => {
    const dx = x - (s.left + s.right) / 2;
    const dy = y - (s.top + s.bottom) / 2;
    const d = dx * dx + dy * dy;
    if (d < bestDistance) {
      bestDistance = d;
      best = i;
    }
  });
  return best;
}

/** Список, где элемент `from` стоит на месте `to` (остальные сдвинуты). */
export function moveIndex<T>(list: readonly T[], from: number, to: number): T[] {
  const next = [...list];
  const [item] = next.splice(from, 1);
  next.splice(Math.max(0, Math.min(to, next.length)), 0, item);
  return next;
}
