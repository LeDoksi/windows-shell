import { useEffect, useState } from "react";
import { backend } from "../shared/backend";
import type { Config, LauncherItem } from "../shared/config";

/** Путь на диске, а не AUMID приложения и не ссылка (то же правило, что `is_path` в Rust). */
export function isPath(target: string): boolean {
  return /[\\/]/.test(target);
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
 * Id плиток, чьих файлов и папок нет на диске (удалили, отключили флешку).
 * Проверяет при каждом новом `check` — например, при открытии окна и после неудачного запуска.
 */
export function useMissing(items: LauncherItem[], check: unknown): ReadonlySet<string> {
  const [missing, setMissing] = useState<ReadonlySet<string>>(new Set());
  // Строка, а не массив: эффект перезапускается, только когда пути правда поменялись.
  const key = JSON.stringify(
    items.filter((item) => item.kind !== "url" && isPath(item.target)).map((i) => [i.id, i.target]),
  );
  useEffect(() => {
    let alive = true;
    const pairs = JSON.parse(key) as [string, string][];
    const ids = pairs.map(([id]) => id);
    const paths = pairs.map(([, path]) => path);
    void backend.call("paths_exist", { paths }).then((exist) => {
      if (alive) setMissing(new Set(ids.filter((_, i) => !exist[i])));
    });
    return () => {
      alive = false;
    };
  }, [key, check]);
  return missing;
}
