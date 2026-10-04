import type { Config } from "../shared/config";
import { moveIndex } from "../shared/ui/dragSlots";
import { addItems, itemFromPath } from "./add";

/**
 * Переставить плитку: внутри категории или в другую. `index` — место плитки в категории
 * после переноса; без него — в конец. Неизвестная плитка или категория — конфиг как был.
 */
export function moveItem(
  config: Config,
  fromId: string,
  itemId: string,
  toId: string,
  index?: number,
): Config {
  const from = config.categories.find((c) => c.id === fromId);
  const at = from?.items.findIndex((i) => i.id === itemId) ?? -1;
  if (!from || at < 0 || !config.categories.some((c) => c.id === toId)) return config;
  if (fromId === toId) {
    const to = index ?? from.items.length - 1;
    if (to === at) return config;
    return {
      ...config,
      categories: config.categories.map((c) =>
        c.id === fromId ? { ...c, items: moveIndex(c.items, at, to) } : c,
      ),
    };
  }
  const item = from.items[at];
  return {
    ...config,
    categories: config.categories.map((c) => {
      if (c.id === fromId) return { ...c, items: c.items.filter((i) => i.id !== itemId) };
      if (c.id !== toId) return c;
      const items = [...c.items];
      items.splice(Math.max(0, Math.min(index ?? items.length, items.length)), 0, item);
      return { ...c, items };
    }),
  };
}

const sameTarget = (a: string, b: string) =>
  a.replace(/[\\/]+$/, "").toLowerCase() === b.replace(/[\\/]+$/, "").toLowerCase();

/**
 * Файлы и папки из Проводника — плитками на место `index` (без него — в конец).
 * `dirs[i]` — папка ли `paths[i]`. То, что уже есть в категории, второй раз не добавляем.
 */
export function dropPaths(
  config: Config,
  categoryId: string,
  paths: string[],
  dirs: boolean[],
  index?: number,
): Config {
  const category = config.categories.find((c) => c.id === categoryId);
  if (!category) return config;
  const seen = category.items.map((i) => i.target);
  const fresh = paths
    .map((path, i) => ({ path, dir: dirs[i] ?? false }))
    .filter(({ path }) => {
      if (seen.some((t) => sameTarget(t, path))) return false;
      seen.push(path);
      return true;
    });
  if (!fresh.length) return config;
  return addItems(
    config,
    categoryId,
    fresh.map(({ path, dir }) => itemFromPath(path, dir)),
    index,
  );
}
