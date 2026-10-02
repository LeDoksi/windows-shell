/** Числовые токены, которые нужны логике (размеры дока, анимации), а не только CSS. */
export const motion = {
  fast: 120,
  base: 180,
  slow: 260,
  /** Пружина увеличения в доке. */
  magnification: { stiffness: 420, damping: 32 },
} as const;

export const dock = {
  iconSize: 52,
  gap: 8,
  /** Во сколько раз растёт иконка под курсором. */
  magnification: 1.55,
  /** Радиус влияния увеличения, в иконках. */
  magnificationRange: 2.5,
} as const;

export const radius = { sm: 8, md: 12, lg: 16, xl: 22 } as const;
