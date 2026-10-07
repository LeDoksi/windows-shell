import type { Config, LauncherItem } from "../shared/config";

/** Новая плитка без id: id выдаёт `addItems`, чтобы он был уникален во всём конфиге. */
export type NewItem = Omit<LauncherItem, "id">;

/** Приложение из «Пуска» (ответ `installed_apps`). */
export type InstalledApp = { name: string; id: string };

/** `url` — интернет-ярлык: игры Steam и Epic кладут на рабочий стол именно такие. */
const programs = new Set(["exe", "lnk", "url", "appref-ms", "bat", "cmd"]);

function lastSegment(path: string) {
  return (
    path
      .replace(/[\\/]+$/, "")
      .split(/[\\/]/)
      .pop() || path
  );
}

/** Плитка для выбранного в диалоге файла или папки. Ярлыки и exe — это приложения. */
export function itemFromPath(path: string, folder: boolean): NewItem {
  const name = lastSegment(path);
  if (folder) return { kind: "folder", title: name, target: path, meta: "Папка" };
  const dot = name.lastIndexOf(".");
  const ext = dot > 0 ? name.slice(dot + 1).toLowerCase() : "";
  if (programs.has(ext)) {
    return { kind: "app", title: name.slice(0, dot), target: path, meta: "Приложение" };
  }
  return {
    kind: "file",
    title: name,
    target: path,
    meta: ext ? `Файл ${ext.toUpperCase()}` : "Файл",
  };
}

export function itemFromApp(app: InstalledApp): NewItem {
  return { kind: "app", title: app.name, target: app.id, meta: "Приложение" };
}

/**
 * Плитка-ссылка. Адрес без схемы считаем сайтом (`ya.ru` → `https://ya.ru`).
 * Пустое название — имя сайта. Не адрес — `null`.
 */
export function itemFromUrl(address: string, title = ""): NewItem | null {
  const text = address.trim();
  if (!text || /\s/.test(text)) return null;
  const withScheme = /^[a-z][a-z0-9+.-]*:/i.test(text) ? text : `https://${text}`;
  let url: URL;
  try {
    url = new URL(withScheme);
  } catch {
    return null;
  }
  const web = url.protocol === "http:" || url.protocol === "https:";
  if (web && !url.hostname.includes(".") && url.hostname !== "localhost") return null;
  const name = title.trim() || url.hostname.replace(/^www\./, "") || text;
  return { kind: "url", title: name, target: url.href, meta: "Сайт" };
}

/** Латиница, цифры и дефисы из названия; кириллица и прочее — просто «item». */
function slug(title: string) {
  return (
    title
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, 24) || "item"
  );
}

function insertAt<T>(list: T[], added: T[], index = list.length): T[] {
  const at = Math.max(0, Math.min(index, list.length));
  return [...list.slice(0, at), ...added, ...list.slice(at)];
}

/** Добавить плитки на место `index` категории (без него — в конец), выдав каждой свободный id. */
export function addItems(
  config: Config,
  categoryId: string,
  items: NewItem[],
  index?: number,
): Config {
  const taken = new Set(config.categories.flatMap((c) => c.items.map((i) => i.id)));
  const withIds = items.map((item) => {
    const base = slug(item.title);
    let id = base;
    for (let n = 2; taken.has(id); n++) id = `${base}-${n}`;
    taken.add(id);
    return { id, ...item };
  });
  return {
    ...config,
    categories: config.categories.map((category) =>
      category.id === categoryId
        ? { ...category, items: insertAt(category.items, withIds, index) }
        : category,
    ),
  };
}
