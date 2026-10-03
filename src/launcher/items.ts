import { useEffect, useState } from "react";
import { backend } from "../shared/backend";
import type { Config, LauncherItem } from "../shared/config";

/**
 * Абсолютный путь на диске, а не AUMID приложения и не ссылка (то же правило, что `is_path`
 * в Rust). У приложений из «Пуска» AUMID бывает с обратными слешами: `{GUID}\\Foo\\foo.exe`.
 */
export function isPath(target: string): boolean {
  return /^([a-z]:[\\/]|\\\\|\/)/i.test(target);
}

/** «Открыть расположение» есть только у того, что лежит на диске. */
export function canReveal(item: LauncherItem): boolean {
  return item.kind !== "url" && isPath(item.target);
}

export function openItem(item: LauncherItem) {
  return backend.call("launch", { kind: item.kind, target: item.target });
}

export function revealItem(item: LauncherItem) {
  return backend.call("reveal", { target: item.target });
}

export function openFolder(path: string) {
  return backend.call("launch", { kind: "folder", target: path });
}

/**
 * Убрать плитку из категории. Меняется только конфиг: сам файл, папка или программа
 * на диске остаются (в Rust команды удаления нет вовсе).
 */
export function removeItem(config: Config, categoryId: string, itemId: string): Config {
  return {
    ...config,
    categories: config.categories.map((category) =>
      category.id === categoryId
        ? { ...category, items: category.items.filter((i) => i.id !== itemId) }
        : category,
    ),
  };
}

/**
 * Id плиток, чьих файлов, папок и приложений нет (удалили, отключили флешку, удалили программу).
 * Проверяет при каждом новом `check` — например, при открытии окна и после неудачного запуска.
 */
export function useMissing(items: LauncherItem[], check: unknown): ReadonlySet<string> {
  const [missing, setMissing] = useState<ReadonlySet<string>>(new Set());
  // Строка, а не массив: эффект перезапускается, только когда цели правда поменялись.
  const key = JSON.stringify(
    items
      .filter((item) => item.kind !== "url")
      .map((i) => [i.id, i.target, isPath(i.target)] as const),
  );
  useEffect(() => {
    let alive = true;
    const all = JSON.parse(key) as [string, string, boolean][];
    const paths = all.filter(([, , path]) => path);
    // Приложение из «Пуска» (AUMID): есть ли оно в системе.
    const apps = all.filter(([, , path]) => !path);
    void Promise.all([
      backend.call("paths_exist", { paths: paths.map(([, t]) => t) }),
      apps.length
        ? backend.call("app_targets", { targets: apps.map(([, t]) => t) })
        : Promise.resolve([]),
    ]).then(([exist, found]) => {
      if (!alive) return;
      setMissing(
        new Set([
          ...paths.filter((_, i) => !exist[i]).map(([id]) => id),
          ...apps.filter((_, i) => found[i] && !found[i].exists).map(([id]) => id),
        ]),
      );
    }, console.error);
    return () => {
      alive = false;
    };
  }, [key, check]);
  return missing;
}
