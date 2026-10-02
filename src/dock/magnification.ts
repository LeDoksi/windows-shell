/** Чистая математика увеличения иконок в доке. */

export type MagnifyOptions = {
  iconSize: number;
  gap: number;
  /** Масштаб иконки прямо под курсором. */
  max: number;
  /** Радиус влияния в иконках. */
  range: number;
};

/**
 * Центры элементов ряда в базовой (неувеличенной) раскладке, от центра ряда.
 * `widths` — ширина каждого элемента: иконки и разделителя.
 */
export function baseCenters(widths: number[], gap: number): number[] {
  const total = widths.reduce((sum, w) => sum + w, 0) + gap * Math.max(widths.length - 1, 0);
  let x = -total / 2;
  return widths.map((w) => {
    const center = x + w / 2;
    x += w + gap;
    return center;
  });
}

/**
 * Целевой масштаб иконок с центрами `centers`.
 * `cursor` — смещение курсора от центра ряда; null — курсора над доком нет.
 */
export function targetScales(cursor: number | null, centers: number[], o: MagnifyOptions) {
  const radius = o.range * (o.iconSize + o.gap);
  return centers.map((center) => {
    if (cursor === null) return 1;
    const d = Math.abs(cursor - center) / radius;
    if (d >= 1) return 1;
    // Косинусная кривая: плавный спад к краю радиуса.
    return 1 + (o.max - 1) * ((Math.cos(Math.PI * d) + 1) / 2);
  });
}

export type Spring = { stiffness: number; damping: number };

/** Один шаг пружины (масса 1, полунеявный Эйлер). */
export function springStep(
  value: number,
  velocity: number,
  target: number,
  dt: number,
  { stiffness, damping }: Spring,
): [number, number] {
  const accel = stiffness * (target - value) - damping * velocity;
  const v = velocity + accel * dt;
  return [value + v * dt, v];
}

export function isSettled(value: number, velocity: number, target: number) {
  return Math.abs(target - value) < 0.001 && Math.abs(velocity) < 0.01;
}
