import type { Category, Config } from "../shared/config";

/** Цвета папок категорий (те же, что в дефолтном конфиге, плюс запас). */
export const categoryColors = [
  "#43B4F5",
  "#B07CF0",
  "#56D27A",
  "#F0A64A",
  "#F06B8E",
  "#4CC3D9",
  "#F2C94C",
  "#8E9AAF",
];

function mapCategory(config: Config, id: string, change: (c: Category) => Category): Config {
  return { ...config, categories: config.categories.map((c) => (c.id === id ? change(c) : c)) };
}

/** Новая пустая категория в конце панели — первым ещё не занятым цветом. */
export function addCategory(
  config: Config,
  name = "Новая категория",
): { config: Config; id: string } {
  const ids = new Set(config.categories.map((c) => c.id));
  let n = config.categories.length + 1;
  while (ids.has(`category-${n}`)) n++;
  const id = `category-${n}`;
  const used = new Set(config.categories.map((c) => c.color.toUpperCase()));
  const color =
    categoryColors.find((c) => !used.has(c)) ??
    categoryColors[config.categories.length % categoryColors.length];
  return {
    id,
    config: { ...config, categories: [...config.categories, { id, name, color, items: [] }] },
  };
}

/** Пустое название не принимаем: у категории на панели должна быть подпись. */
export function renameCategory(config: Config, id: string, name: string): Config {
  const trimmed = name.trim();
  return trimmed ? mapCategory(config, id, (c) => ({ ...c, name: trimmed })) : config;
}

export function recolorCategory(config: Config, id: string, color: string): Config {
  return mapCategory(config, id, (c) => ({ ...c, color }));
}

/** Сдвинуть категорию на панели: -1 — левее, +1 — правее. У края ничего не меняется. */
export function moveCategory(config: Config, id: string, delta: -1 | 1): Config {
  const from = config.categories.findIndex((c) => c.id === id);
  const to = from + delta;
  if (from < 0 || to < 0 || to >= config.categories.length) return config;
  const categories = [...config.categories];
  [categories[from], categories[to]] = [categories[to], categories[from]];
  return { ...config, categories };
}

/** Убрать категорию с панели вместе с плитками. Файлы на диске не трогаются. */
export function removeCategory(config: Config, id: string): Config {
  return { ...config, categories: config.categories.filter((c) => c.id !== id) };
}
